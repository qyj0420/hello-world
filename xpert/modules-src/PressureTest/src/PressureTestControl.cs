using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace HVX.Controls
{
    [ToolboxItem(true)]
    [DefaultProperty("BaseU0VoltageKv")]
    public sealed partial class PressureTestControl : UserControl
    {
        // The dimensions below distinguish the complete LabVIEW window from the
        // drawable .NET control area.  LabVIEW's title, menu and path/tool rows
        // consume approximately 120 vertical pixels in the target application.
        public const int DesignWindowWidth = 1920;
        public const int DesignWindowHeight = 1080;
        public const int MinimumWindowWidth = 1280;
        public const int MinimumWindowHeight = 720;
        public const int LabViewChromeReservedHeight = 120;
        public const int ReferenceWidth = DesignWindowWidth;
        public const int ReferenceHeight = DesignWindowHeight - LabViewChromeReservedHeight;
        public const int MinimumWidth = MinimumWindowWidth;
        public const int MinimumHeight = MinimumWindowHeight - LabViewChromeReservedHeight;

        static readonly string[] RangeCodes =
        {
            "PICO:0", "PICO:1", "PICO:2", "PICO:3", "PICO:4", "PICO:5", "PICO:6",
            "PICO:7", "PICO:8", "PICO:9", "PICO:10"
        };

        string[] RangeLabels { get { return CreateCalibratedRangeLabels(); } }

        static readonly string[] SpectrumCodes =
        {
            "Q-L", "N-L", "Q-PHI", "N-PHI", "F-T",
            "U-Q-L", "U-N-L", "n-Q-U", "n-N-U", "PRPD", "L-PRPD"
        };

        readonly object stateLock = new object();
        readonly object uiDispatchLock = new object();
        readonly Queue<UiDispatchRequest> pendingUiRequests = new Queue<UiDispatchRequest>();
        readonly List<UiDispatchRequest> outstandingUiRequests = new List<UiDispatchRequest>();
        readonly object eventDispatchLock = new object();
        readonly Queue<EventHandler> pendingEventNotifications = new Queue<EventHandler>();
        readonly int ownerThreadId;
        int handleGeneration;
        int disposeStarted;
        bool eventDispatchInProgress;
        bool suppressUiEvents;
        bool positivePolarity = true;
        bool planMode = true;
        bool advancedMode = true;
        bool automaticPressureEnabled = true;
        bool automaticRange = true;
        bool planVoltagesArePeak;
        bool highVoltageClosed = true;
        bool embeddedMode;
        bool simulationMode;
        string selectedRangeCode = "UNCALIBRATED";
        string customVoltageModeCode = "U0";
        string pressureStateCode = "WAITING";
        string selectedChannelCode = "Q-L";
        string selectedPhaseCode = "A";
        string waveformWindowCode = "MAXIMUM";
        string projectName = "测试-测试";
        string lastActionCode = String.Empty;
        string lastError = String.Empty;
        string projectPlanError = String.Empty;
        string lastPlanDiagnostic = String.Empty;
        string analysisStatus = "NOT_ANALYZED";
        double baseU0VoltageKv = 8.4;
        double customVoltageValue = 0.5;
        double requestedTargetPeakVoltageKv;
        double actualPeakVoltageKv = Double.NaN;
        double inductanceH = 1.0;
        double maxDischargePc;
        double equivalentCapacitanceUf;
        double oscillationFrequencyHz;
        double dissipationFactorPercent;
        double locationConfidencePercent;
        bool locationFilterEnabled;
        double locationFilterStrength = 40.0;
        double cableLengthMeters = 175.4;
        double[] lastVoltageKv = new double[0];
        double[] lastDischargePc = new double[0];
        double lastSampleRateHz = 1.0;
        double[] planMultipliers = new double[0];
        int[] planRepetitions = new int[0];
        double[] planVoltagesKv = new double[0];
        int selectedPlanStepIndex = -1;
        int currentPlanRepetition = 1;

        long polarityChangedSequence;
        long programModeChangedSequence;
        long voltageSettingChangedSequence;
        long advancedModeChangedSequence;
        long automaticPressureChangedSequence;
        long rangeModeChangedSequence;
        long rangeChangedSequence;
        long pressureRequestSequence;
        long deleteRequestSequence;
        long channelChangedSequence;
        long phaseChangedSequence;
        long waveformWindowChangedSequence;
        long realtimeDataSequence;
        long locationDataSequence;
        long locationFilterChangedSequence;

        public PressureTestControl()
        {
            ownerThreadId = Thread.CurrentThread.ManagedThreadId;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoScaleMode = AutoScaleMode.None;
            BackColor = PressureTestTheme.Workspace;
            ForeColor = PressureTestTheme.Ink;
            Font = PressureTestTheme.Font(9.5f);
            // The host owns the viewport; a standalone-window minimum must not
            // force the embedded control beyond the desktop application's client area.
            MinimumSize = new Size(800, 400);
            Size = new Size(ReferenceWidth, ReferenceHeight);
            InitializeLayout();
            InitializePressureThresholdGuide();
            ApplyAllStateToUi();
            InitializePressurePreferences();
            InitializeP04Feedback();
        }

        public event EventHandler PolarityChanged;
        public event EventHandler ProgramModeChanged;
        public event EventHandler VoltageSettingChanged;
        public event EventHandler AdvancedModeChanged;
        public event EventHandler AutomaticPressureChanged;
        public event EventHandler RangeModeChanged;
        public event EventHandler RangeChanged;
        public event EventHandler PressureRequested;
        public event EventHandler DeleteRequested;
        public event EventHandler ChannelChanged;
        public event EventHandler PhaseChanged;
        public event EventHandler WaveformWindowChanged;
        public event EventHandler RealtimeDataUpdated;
        public event EventHandler LocationDataUpdated;
        public event EventHandler LocationFilterChanged;

        /// <summary>
        /// Compatibility event name retained for existing LabVIEW callers.
        /// SpectrumChanged is the preferred name for the ten analysis views.
        /// </summary>
        public event EventHandler SpectrumChanged;

        [Category("布局")]
        [DefaultValue(false)]
        public bool EmbeddedMode
        {
            get { return embeddedMode; }
            set { RunOnUiThread(delegate { embeddedMode = value; UpdateEmbeddedLayout(); }); }
        }

        [Category("显示")]
        [DefaultValue(false)]
        public bool SimulationMode
        {
            get { return simulationMode; }
            set { RunOnUiThread(delegate { if(PressureParametersLocked)return;if(simulationMode!=value){simulationMode=value;ClearPressureInitialRangeCache();pressureNoiseInitializationReady=false;ResetFinishCurrentRound();ResetP04FeedbackSession();lock(stateLock){InvalidateDischargeCalibrationNoLock();highVoltageFeedbackKnown=false;highVoltageClosed=true;}ResetLocationCycleAcquisition();InvalidateNoiseReferenceContext();CancelAutomaticPressureSchedulingCore();}UpdateRangeUi();UpdatePressureUi(); }); }
        }

        [Category("加压设置")]
        [DefaultValue(true)]
        public bool PositivePolarity
        {
            get { lock (stateLock) return positivePolarity; }
            set { SetPolarity(value); }
        }

        [Category("加压设置")]
        [DefaultValue(true)]
        public bool VlfPositivePressurePolarity
        {
            get { return PositivePolarity; }
            set { SetPolarity(value); }
        }

        [Category("加压设置")]
        [DefaultValue("PLAN")]
        public string PressureProgramModeCode
        {
            get { lock (stateLock) return planMode ? "PLAN" : "CUSTOM"; }
            set { SetPressureProgramMode(value); }
        }

        [Category("加压设置")]
        [DefaultValue(true)]
        public bool AdvancedMode
        {
            get { lock (stateLock) return advancedMode; }
            set { SetAdvancedMode(value); }
        }

        [Category("加压设置")]
        [DefaultValue(true)]
        public bool AutomaticPressureEnabled
        {
            get { lock (stateLock) return automaticPressureEnabled; }
            set { SetAutomaticPressure(value); }
        }

        [Category("量程")]
        [DefaultValue(true)]
        public bool AutomaticRange
        {
            get { lock (stateLock) return automaticRange; }
            set { SetAutomaticRange(value); }
        }

        [Category("量程")]
        [DefaultValue("UNCALIBRATED")]
        public string SelectedRangeCode
        {
            get { lock (stateLock) return selectedRangeCode; }
            set { SetSelectedRange(value); }
        }

        [Category("加压设置")]
        [DefaultValue(8.4)]
        public double BaseU0VoltageKv
        {
            get { lock (stateLock) return baseU0VoltageKv; }
            set
            {
                if (!IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException("value");
                RunOnUiThread(delegate
                {
                    if (PressureParametersLocked) return;
                    bool normalizeCustomMinimum;
                    lock (stateLock)
                    {
                        baseU0VoltageKv = value;
                        normalizeCustomMinimum = customVoltageValue > 0 && customVoltageValue * CustomVoltagePeakFactorNoLock() < MinimumCustomPeakVoltageKv;
                    }
                    if (normalizeCustomMinimum) SetCustomVoltageValue(CustomVoltageValue);
                    RecalculatePlanVoltagesWhenDerived();
                    RaiseVoltageSettingChanged();
                });
            }
        }

        [Category("加压设置")]
        [DefaultValue(false)]
        public bool PlanVoltagesArePeak
        {
            get { lock (stateLock) return planVoltagesArePeak; }
            set
            {
                RunOnUiThread(delegate
                {
                    if (PressureParametersLocked) return;
                    lock (stateLock) planVoltagesArePeak = value;
                    RefreshPlanGrid();
                    RaiseVoltageSettingChanged();
                });
            }
        }

        [Category("加压设置")]
        [DefaultValue("U0")]
        public string CustomVoltageModeCode
        {
            get { lock (stateLock) return customVoltageModeCode; }
            set { SetCustomVoltageMode(value); }
        }

        [Category("加压设置")]
        [DefaultValue(0.5)]
        public double CustomVoltageValue
        {
            get { lock (stateLock) return customVoltageValue; }
            set { SetCustomVoltageValue(value); }
        }

        [Category("分析输入")]
        [DefaultValue(1.0)]
        public double InductanceH
        {
            get { lock (stateLock) return inductanceH; }
            set
            {
                if (!IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException("value");
                RunOnUiThread(delegate
                {
                    if (PressureParametersLocked) return;
                    lock (stateLock) inductanceH = value;
                    AnalyzeCurrentWaveforms();
                });
            }
        }

        [Browsable(false)] public double CalculatedTargetPeakVoltageKv { get { return CalculateTargetPeakVoltageKv(); } }
        [Browsable(false)] public double RequestedTargetPeakVoltageKv { get { lock (stateLock) return requestedTargetPeakVoltageKv; } }
        [Browsable(false)] public double ActualPeakVoltageKv { get { lock (stateLock) return actualPeakVoltageKv; } }
        [Browsable(false)] public string PressureStateCode { get { lock (stateLock) return pressureStateCode; } set { SetPressureState(value, ActualPeakVoltageKv); } }
        [Browsable(false)] public string SelectedChannelCode { get { lock (stateLock) return selectedChannelCode; } set { SetSelectedChannel(value); } }
        [Browsable(false)] public string SelectedPhaseCode { get { lock (stateLock) return selectedPhaseCode; } set { SetSelectedPhase(value); } }
        [Category("测量设置")]
        [DefaultValue("MAXIMUM")]
        public string WaveformWindowCode { get { lock (stateLock) return waveformWindowCode; } set { SetWaveformWindow(value); } }
        [Browsable(false)] public int SelectedPlanStepIndex { get { lock (stateLock) return selectedPlanStepIndex; } set { SetSelectedPlanStep(value); } }
        [Browsable(false)] public int CurrentPlanRepetition { get { lock (stateLock) return currentPlanRepetition; } set { SetCurrentPlanRepetition(value); } }
        [Browsable(false)] public int CurrentPlanRepetitionTotal { get { lock (stateLock) return CurrentPlanRepetitionTotalNoLock(); } }
        [Category("任务信息")]
        [DefaultValue("测试-测试")]
        public string ProjectName
        {
            get { lock (stateLock) return projectName; }
            set
            {
                string next = String.IsNullOrWhiteSpace(value) ? "未命名任务" : value.Trim();
                RunOnUiThread(delegate { lock (stateLock) projectName = next; UpdateTaskInformationUi(); });
            }
        }
        [Category("任务信息")]
        [DefaultValue(175.4)]
        public double CableLengthMeters
        {
            get { lock (stateLock) return cableLengthMeters; }
            set
            {
                if (!IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException("value");
                RunOnUiThread(delegate { if(PressureParametersLocked)return;lock (stateLock) cableLengthMeters = value; UpdateTaskInformationUi(); });
            }
        }
        [Browsable(false)] public bool HighVoltageClosed { get { lock (stateLock) return IsHighVoltageFeedbackKnownNoLock() && !IsHighVoltagePermissionNoLock(); } set { ReportHighVoltageState(true, !value); } }
        [Browsable(false)] public string LastActionCode { get { lock (stateLock) return lastActionCode; } }
        [Browsable(false)] public string LastError { get { lock (stateLock) return lastError; } }
        [Browsable(false)] public string LastPlanDiagnostic { get { lock (stateLock) return lastPlanDiagnostic; } }
        [Browsable(false)] public Action<string> DiagnosticSink { get; set; }
        [Browsable(false)] public string AnalysisStatus { get { lock (stateLock) return analysisStatus; } }
        [Browsable(false)] public double PartialDischargeMaximumPc { get { lock (stateLock) return maxDischargePc; } }
        [Browsable(false)] public double EquivalentCapacitanceUf { get { lock (stateLock) return equivalentCapacitanceUf; } }
        [Browsable(false)] public double OscillationFrequencyHz { get { lock (stateLock) return oscillationFrequencyHz; } }
        [Browsable(false)] public double DissipationFactorPercent { get { lock (stateLock) return dissipationFactorPercent; } }
        [Browsable(false)] public double LocationConfidencePercent { get { lock (stateLock) return locationConfidencePercent; } }
        [Category("定位过滤")]
        [DefaultValue(false)]
        public bool LocationFilterEnabled
        {
            get { lock (stateLock) return locationFilterEnabled; }
            set { SetLocationFilterEnabled(value); }
        }

        [Category("定位过滤")]
        [DefaultValue(40.0)]
        public double LocationFilterStrength
        {
            get { lock (stateLock) return locationFilterStrength; }
            set { SetLocationFilterStrength(value); }
        }

        [Browsable(false)] public string SelectedSpectrumCode { get { return SelectedChannelCode; } set { SetSelectedSpectrum(value); } }

        [Browsable(false)] public long PolarityChangedSequence { get { lock (stateLock) return polarityChangedSequence; } }
        [Browsable(false)] public long ProgramModeChangedSequence { get { lock (stateLock) return programModeChangedSequence; } }
        [Browsable(false)] public long VoltageSettingChangedSequence { get { lock (stateLock) return voltageSettingChangedSequence; } }
        [Browsable(false)] public long AdvancedModeChangedSequence { get { lock (stateLock) return advancedModeChangedSequence; } }
        [Browsable(false)] public long AutomaticPressureChangedSequence { get { lock (stateLock) return automaticPressureChangedSequence; } }
        [Browsable(false)] public long RangeModeChangedSequence { get { lock (stateLock) return rangeModeChangedSequence; } }
        [Browsable(false)] public long RangeChangedSequence { get { lock (stateLock) return rangeChangedSequence; } }
        [Browsable(false)] public long PressureRequestSequence { get { lock (stateLock) return pressureRequestSequence; } }
        [Browsable(false)] public long DeleteRequestSequence { get { lock (stateLock) return deleteRequestSequence; } }
        [Browsable(false)] public long ChannelChangedSequence { get { lock (stateLock) return channelChangedSequence; } }
        [Browsable(false)] public long PhaseChangedSequence { get { lock (stateLock) return phaseChangedSequence; } }
        [Browsable(false)] public long WaveformWindowChangedSequence { get { lock (stateLock) return waveformWindowChangedSequence; } }
        [Browsable(false)] public long RealtimeDataSequence { get { lock (stateLock) return realtimeDataSequence; } }
        [Browsable(false)] public long LocationDataSequence { get { lock (stateLock) return locationDataSequence; } }
        [Browsable(false)] public long LocationFilterChangedSequence { get { lock (stateLock) return locationFilterChangedSequence; } }
        [Browsable(false)] public long SpectrumChangedSequence { get { return ChannelChangedSequence; } }

        public string[] GetRangeCodes() { return (string[])RangeCodes.Clone(); }
        public string[] GetRangeLabels() { return (string[])RangeLabels.Clone(); }
        public string[] GetSpectrumCodes() { return (string[])SpectrumCodes.Clone(); }
        public double[] GetPressurePlanMultipliers() { lock (stateLock) return (double[])planMultipliers.Clone(); }
        public int[] GetPressurePlanRepetitions() { lock (stateLock) return (int[])planRepetitions.Clone(); }
        public double[] GetPressurePlanVoltagesKv() { lock (stateLock) return (double[])planVoltagesKv.Clone(); }

        public void SetPolarity(bool positive)
        {
            RunOnUiThread(delegate
            {
                if (PressureParametersLocked) return;
                bool changed;
                lock (stateLock)
                {
                    changed = positivePolarity != positive;
                    positivePolarity = positive;
                    if (changed) polarityChangedSequence++;
                }
                UpdatePolarityUi();
                if (changed)
                {
                    Raise(PolarityChanged);
                }
            });
        }

        public void SetVlfPressurePolarity(bool positive)
        {
            SetPolarity(positive);
        }

        public void SetLocationFilterEnabled(bool enabled)
        {
            RunOnUiThread(delegate
            {
                bool changed;
                lock (stateLock)
                {
                    changed = locationFilterEnabled != enabled;
                    locationFilterEnabled = enabled;
                    if (changed) locationFilterChangedSequence++;
                }
                UpdateLocationFilterUi();
                if (changed) Raise(LocationFilterChanged);
            });
        }

        public void SetLocationFilterStrength(double strength)
        {
            if (!IsFinite(strength) || strength < 0.0 || strength > 100.0)
                throw new ArgumentOutOfRangeException("strength", "Location filter strength must be between 0 and 100.");
            RunOnUiThread(delegate
            {
                double normalized = Math.Round(strength, 0, MidpointRounding.AwayFromZero);
                bool changed;
                lock (stateLock)
                {
                    changed = Math.Abs(locationFilterStrength - normalized) > 1e-12 || !locationFilterEnabled;
                    locationFilterStrength = normalized;
                    locationFilterEnabled = true;
                    if (changed) locationFilterChangedSequence++;
                }
                UpdateLocationFilterUi();
                if (changed) Raise(LocationFilterChanged);
            });
        }

        public double[] ReadLocationFilterState()
        {
            lock (stateLock) return new[] { locationFilterEnabled ? 1.0 : 0.0, locationFilterStrength };
        }

        public void SetPressureProgramMode(string modeCode)
        {
            string normalized = NormalizeProgramMode(modeCode);
            RunOnUiThread(delegate
            {
                if (PressureParametersLocked) return;
                bool next = normalized == "PLAN" && planVoltagesKv.Length > 0;
                bool changed;
                lock (stateLock)
                {
                    changed = planMode != next;
                    planMode = next;
                    if (changed) programModeChangedSequence++;
                }
                if (!next) { CancelAutomaticPressureSchedulingCore(); SetAdvancedMode(true); }
                UpdateProgramUi();
                UpdateAdvancedUi();
                UpdatePressureUi();
                if (changed) Raise(ProgramModeChanged);
            });
        }

        public void SetAdvancedMode(bool enabled)
        {
            RunOnUiThread(delegate
            {
                if (PressureParametersLocked) return;
                // Choosing a plan mode is not an electrical permission. In
                // particular it must be possible to leave the DI-free manual
                // zero-input mode so the host can establish supervised input.
                // RequestPressure/countdown still require current P0.4 HIGH.
                if (!enabled && (!planMode || planVoltagesKv.Length == 0)) { UpdateAdvancedUi(); return; }
                if (!enabled) ResetFinishCurrentRound();
                bool changed;
                lock (stateLock)
                {
                    changed = advancedMode != enabled;
                    advancedMode = enabled;
                    if (changed) advancedModeChangedSequence++;
                }
                UpdateAdvancedUi();
                if (!enabled) SetAutomaticRange(true);
                UpdateRangeUi();
                if (changed) Raise(AdvancedModeChanged);
            });
        }

        public void SetAutomaticPressure(bool enabled)
        {
            SetAutomaticPostProcess(enabled);
        }

        public void SetAutomaticRange(bool enabled)
        {
            RunOnUiThread(delegate
            {
                if (PressureParametersLocked || !enabled && !AdvancedMode) { UpdateRangeUi(); return; }
                bool changed;
                lock (stateLock)
                {
                    changed = automaticRange != enabled;
                    automaticRange = enabled;
                    if (changed) rangeModeChangedSequence++;
                }
                UpdateRangeUi();
                if (changed) { InvalidateNoiseReferenceContext();RefreshManualRangeRestartForAutomaticMode();Raise(RangeModeChanged); }
            });
        }

        public void SetSelectedRange(string rangeCode)
        {
            string normalized = NormalizeRange(rangeCode);
            RunOnUiThread(delegate
            {
                if (PressureParametersLocked || !AdvancedMode) return;
                bool changed;
                lock (stateLock)
                {
                    if(normalized!="UNCALIBRATED"&&!HasDischargeCalibrationNoLock())throw new InvalidOperationException("A valid pC/V calibration for this phase is required.");
                    if(normalized!="UNCALIBRATED"&&Array.IndexOf(RangeCodes,normalized)<PressureChargeRangeFloor.MinimumIndex(calibrationPcPerVolt))
                        throw new ArgumentOutOfRangeException("rangeCode","局放量程不能小于100 pC。");
                    changed = !String.Equals(selectedRangeCode, normalized, StringComparison.Ordinal);
                    selectedRangeCode = normalized;
                    if (changed) rangeChangedSequence++;
                }
                UpdateRangeUi();
                if (changed) { InvalidateNoiseReferenceContext();Raise(RangeChanged); }
            });
        }

        public void SetCustomVoltageMode(string modeCode)
        {
            string normalized = NormalizeVoltageMode(modeCode);
            RunOnUiThread(delegate
            {
                if (PressureParametersLocked) return;
                bool changed;
                lock (stateLock)
                {
                    changed = !String.Equals(customVoltageModeCode, normalized, StringComparison.Ordinal);
                    customVoltageModeCode = normalized;
                }
                SetCustomVoltageValue(CustomVoltageValue);
                UpdateCustomVoltageUi();
                if (changed) RaiseVoltageSettingChanged();
            });
        }

        public void SetCustomVoltageValue(double value)
        {
            if (!IsFinite(value) || value < 0 || value > 999.9) throw new ArgumentOutOfRangeException("value");
            RunOnUiThread(delegate
            {
                if (PressureParametersLocked) return;
                bool changed;
                lock (stateLock)
                {
                    double factor = CustomVoltagePeakFactorNoLock();
                    double minimum = MinimumCustomVoltageValueNoLock();
                    bool belowMinimum = value > 0 && value * factor < MinimumCustomPeakVoltageKv;
                    // Explicit zero remains a non-energizing reference request.
                    // Never turn an attempted positive setting into that request.
                    if (belowMinimum && IsFinite(minimum) && minimum <= 999.9) value = minimum;
                    // Do not silently turn a requested nonzero voltage into 0
                    // before a hardware limit has been configured. Keep the
                    // entered value visible; RequestPressure still refuses to
                    // publish nonzero HV until a valid hard limit exists.
                    if (maximumPeakVoltageKv > 0 && IsFinite(factor) && factor > 0 && value * factor > maximumPeakVoltageKv)
                    {
                        if (maximumPeakVoltageKv < MinimumCustomPeakVoltageKv)
                            voltageLimitMessage = PressureUiText("设备限压低于最低峰值 1 kV，暂不能加压", "The device limit is below the 1 kV minimum peak; pressure is unavailable.");
                        else
                        {
                            voltageLimitMessage = "目标峰值超过设备限压，已限制输入 / Target peak limited";
                            value = Math.Max(minimum, Math.Floor(maximumPeakVoltageKv / factor * 10.0) / 10.0);
                        }
                    }
                    else voltageLimitMessage = belowMinimum
                        ? PressureUiText("非零电压最低峰值为 1 kV", "The minimum nonzero peak voltage is 1 kV.") : String.Empty;
                    changed = Math.Abs(customVoltageValue - value) > 0.0000001;
                    customVoltageValue = value;
                }
                UpdateCustomVoltageUi();
                UpdatePostProcessUi();
                if (changed) RaiseVoltageSettingChanged();
            });
        }

        public void SetPressurePlan(double[] u0Multipliers, int[] repetitions, double[] voltagesKv)
        {
            if (u0Multipliers == null || repetitions == null || u0Multipliers.Length == 0 ||
                u0Multipliers.Length != repetitions.Length)
                throw new ArgumentException("u0Multipliers and repetitions must have the same non-zero length.");
            if (voltagesKv != null && voltagesKv.Length != u0Multipliers.Length)
                throw new ArgumentException("voltagesKv must be null or match the pressure-plan length.", "voltagesKv");
            double[] multiplierCopy = new double[u0Multipliers.Length];
            int[] repetitionCopy = new int[repetitions.Length];
            double[] voltageCopy = new double[u0Multipliers.Length];
            for (int i = 0; i < u0Multipliers.Length; i++)
            {
                if (!IsFinite(u0Multipliers[i]) || u0Multipliers[i] < 0) throw new ArgumentOutOfRangeException("u0Multipliers");
                if (repetitions[i] <= 0) throw new ArgumentOutOfRangeException("repetitions");
                double voltage = voltagesKv == null ? u0Multipliers[i] * BaseU0VoltageKv : voltagesKv[i];
                if (!IsFinite(voltage) || voltage < 0) throw new ArgumentOutOfRangeException("voltagesKv");
                multiplierCopy[i] = u0Multipliers[i];
                repetitionCopy[i] = repetitions[i];
                voltageCopy[i] = voltage;
            }
            RunOnUiThread(delegate
            {
                if (PressureParametersLocked) return;
                lock (stateLock)
                {
                    planMultipliers = multiplierCopy;
                    planRepetitions = repetitionCopy;
                    planVoltagesKv = voltageCopy;
                    projectPlanError = String.Empty;
                    selectedPlanStepIndex = Math.Min(multiplierCopy.Length - 1, multiplierCopy.Length > 1 ? 1 : 0);
                    currentPlanRepetition = 1;
                }
                RefreshPlanGrid();
                RaiseVoltageSettingChanged();
            });
        }

        public void SetPressurePlan(double[] u0Multipliers, int[] repetitions)
        {
            SetPressurePlan(u0Multipliers, repetitions, null);
        }

        public bool LoadProjectPlan(string projectPath)
        {
            if (PressureParametersLocked) return false;
            try
            {
                if (String.IsNullOrWhiteSpace(projectPath)) throw new ArgumentException("A project path is required.", "projectPath");
                string itemPath = Directory.Exists(projectPath) ? Path.Combine(projectPath, "ITEM.in") : projectPath;
                if (!File.Exists(itemPath)) throw new FileNotFoundException("ITEM.in was not found.", itemPath);
                ProjectPressurePlan plan;
                string error;
                if (!ProjectPlanReader.TryRead(itemPath, out plan, out error))
                    throw new InvalidDataException(error);
                double[] multipliers = plan.Rows.Select(row => row.U0Multiplier).ToArray();
                int[] repetitions = plan.Rows.Select(row => row.RepetitionCount).ToArray();
                double[] voltages = plan.Rows.Select(row => row.VoltageKv).ToArray();
                string nextProjectPath = Path.GetDirectoryName(Path.GetFullPath(itemPath));
                bool retainCustomMode;
                lock (stateLock) retainCustomMode = !planMode &&
                    String.Equals(ProjectPath, nextProjectPath, StringComparison.OrdinalIgnoreCase);
                bool applied = false;
                RunOnUiThread(delegate
                {
                    if (PressureParametersLocked) return;
                    ResetFinishCurrentRound();
                    ProjectPath = nextProjectPath;
                    ClearDischargeCalibration(); // A loaded plan cannot authenticate another project's pC/V calibration.
                    if (plan.IsFreeValue) ClearPressurePlan();
                    lock (stateLock)
                    {
                        baseU0VoltageKv = plan.PhaseVoltageKv;
                        planVoltagesArePeak = false; // ITEM values match the project's calculated RMS voltage column.
                    }
                    if (!plan.IsFreeValue)
                    {
                        SetPressurePlan(multipliers, repetitions, voltages);
                        SetSelectedPlanStep(0); // Start at the first saved row, including zero voltage.
                    }
                    SetPressureProgramMode(plan.IsFreeValue || retainCustomMode ? "CUSTOM" : "PLAN");
                    lock (stateLock) { projectPlanError = lastError = lastPlanDiagnostic = String.Empty; lastActionCode = "LOAD_PROJECT_PLAN"; }
                    UpdatePressureUi();
                    applied = true;
                });
                return applied;
            }
            catch (Exception exception)
            {
                string message = String.IsNullOrWhiteSpace(projectPath)
                    ? PressureUiText("未加载项目加压计划，请先加载项目。", "No project pressure plan is loaded. Load a project first.")
                    : PressureUiText("无法读取项目加压计划，请检查项目相电压和加压步骤后重新加载。", "Unable to load the pressure plan. Check the project phase voltage and pressure steps, then reload the project.");
                string diagnostic = exception.ToString();
                lock (stateLock) lastPlanDiagnostic = diagnostic;
                try
                {
                    Action<string> sink = DiagnosticSink;
                    if (sink != null) sink(diagnostic);
                    else Trace.TraceError(diagnostic);
                }
                catch (Exception loggingError) { Trace.TraceError("Pressure plan diagnostic logging failed: " + loggingError); }
                RunOnUiThread(delegate
                {
                    if (PressureParametersLocked) return;
                    ClearPressurePlan();
                    // Do not leave another project's path or U0 available after a failed load.
                    ProjectPath = String.Empty;
                    ClearDischargeCalibration();
                    lock (stateLock) { projectPlanError = lastError = message; lastActionCode = "LOAD_PROJECT_PLAN_FAILED"; }
                    UpdatePressureUi();
                });
                return false;
            }
        }

        public void ClearPressurePlan()
        {
            RunOnUiThread(delegate
            {
                if (PressureParametersLocked) return;
                lock (stateLock)
                {
                    planMultipliers = new double[0];
                    planRepetitions = new int[0];
                    planVoltagesKv = new double[0];
                    selectedPlanStepIndex = -1;
                    currentPlanRepetition = 1;
                    baseU0VoltageKv = Double.NaN;
                    planMode = false;
                    planVoltagesArePeak = false;
                }
                CancelAutomaticPressureSchedulingCore();
                SetAdvancedMode(true);
                RefreshPlanGrid();
                UpdateProgramUi();
                RaiseVoltageSettingChanged();
            });
        }

        public void SetSelectedPlanStep(int index)
        {
            RunOnUiThread(delegate
            {
                if (PressureParametersLocked) return;
                int count;
                lock (stateLock) count = planMultipliers.Length;
                if (index < 0 || index >= count) throw new ArgumentOutOfRangeException("index");
                bool changed;
                lock (stateLock)
                {
                    changed = selectedPlanStepIndex != index;
                    selectedPlanStepIndex = index;
                    if (changed) currentPlanRepetition = 1;
                }
                SelectPlanGridRow(index);
                if (changed) RaiseVoltageSettingChanged();
            });
        }

        public void SetCurrentPlanRepetition(int repetition)
        {
            RunOnUiThread(delegate
            {
                if (PressureParametersLocked) return;
                lock (stateLock)
                {
                    int total = CurrentPlanRepetitionTotalNoLock();
                    if (repetition < 1 || repetition > total) throw new ArgumentOutOfRangeException("repetition");
                    currentPlanRepetition = repetition;
                }
                UpdatePressureUi();
            });
        }

        public void SetWaveforms(double[] voltageKv, double[] partialDischargePc, double sampleRateHz)
        {InstallAcquisitionWaveforms(voltageKv,partialDischargePc,sampleRateHz,null);}

        void InstallAcquisitionWaveforms(double[] voltageKv, double[] partialDischargePc, double sampleRateHz, System.Collections.Generic.IDictionary<string,object> metadata)
        {
            if (voltageKv == null) throw new ArgumentNullException("voltageKv");
            if (partialDischargePc == null) throw new ArgumentNullException("partialDischargePc");
            if (!IsFinite(sampleRateHz) || sampleRateHz <= 0) throw new ArgumentOutOfRangeException("sampleRateHz");
            double[] voltageCopy = (double[])voltageKv.Clone();
            double[] dischargeCopy = (double[])partialDischargePc.Clone();
            RunOnUiThread(delegate
            {
                ValidateLocationAcquisitionIdentity(metadata);
                InstallReferencePreviewContext(metadata);
                InstallNoiseContext(dischargeCopy,sampleRateHz,metadata);
                lock (stateLock)
                {
                    lastVoltageKv = voltageCopy;
                    lastDischargePc = dischargeCopy;
                    lastSampleRateHz = sampleRateHz;
                }
                waveformView.SetData(voltageCopy, sampleRateHz, dischargeCopy, sampleRateHz);
                AnalyzeCurrentWaveforms();
                EvaluateLocationCycleWindow();
                UpdateWaveformWindowUi();
            });
        }

        public void ApplyRealtimeMetrics(double pdMaxPc, double capacitanceUf, double frequencyHz, double tanDeltaPercent)
        {
            RunOnUiThread(delegate
            {
                if(currentAcquisitionReferencePreview){ApplyReferencePreviewMetrics();return;}
                if(currentAcquisitionReferenceOnly){ApplyNoiseReferenceMetrics();return;}
                if(currentZeroVoltageAnalysis){ApplyZeroVoltageAnalysisMetrics();return;}
                metricFrequencyAvailable = IsFinite(frequencyHz) && frequencyHz >= 0;
                rawMeasuredCapacitanceUf = capacitanceUf;
                metricCapacitanceAvailable = IsFinite(capacitanceUf);
                rawMeasuredTanDeltaPercent = tanDeltaPercent;
                metricTanDeltaAvailable = IsFinite(tanDeltaPercent);
                lock (stateLock)
                {
                    maxDischargePc = CleanMetric(pdMaxPc);
                    equivalentCapacitanceUf = CleanMetric(capacitanceUf);
                    oscillationFrequencyHz = CleanMetric(frequencyHz);
                    dissipationFactorPercent = CleanMetric(tanDeltaPercent);
                    realtimeDataSequence++;
                    analysisStatus = "HOST_APPLIED";
                    lastError = String.Empty;
                }
                UpdateMetricsUi();
                UpdateWaveformWindowUi();
                Raise(RealtimeDataUpdated);
                MaybeShowFrequencyReminder();
            });
        }

        public void SetLocationData(double[] positionMetres, double[] partialDischargePc,
            double cableLengthMetres, double confidencePercent)
        {
            if (positionMetres == null) throw new ArgumentNullException("positionMetres");
            if (partialDischargePc == null) throw new ArgumentNullException("partialDischargePc");
            double[] positionCopy = (double[])positionMetres.Clone();
            double[] dischargeCopy = (double[])partialDischargePc.Clone();
            CompactFiniteLocationData(ref positionCopy, ref dischargeCopy);
            RunOnUiThread(delegate
            {
                if(currentAcquisitionReferenceOnly||currentAcquisitionReferencePreview){spectrumDashboardView.ClearData();return;}
                if (spectrumDashboardView != null)
                    spectrumDashboardView.SetOwnedLocationData(positionCopy, dischargeCopy, cableLengthMetres);
                lock (stateLock)
                {
                    locationConfidencePercent = IsFinite(confidencePercent) ? Math.Max(0, Math.Min(100, confidencePercent)) : 0;
                    locationDataSequence++;
                }
                Raise(LocationDataUpdated);
            });
        }

        public void SetLocationData(double[] positionMetres, double[] partialDischargePc, double cableLengthMetres)
        {
            SetLocationData(positionMetres, partialDischargePc, cableLengthMetres, LocationConfidencePercent);
        }

        public double[] ReadRealtimeMetrics()
        {
            lock (stateLock)
                return new[] { maxDischargePc, equivalentCapacitanceUf, oscillationFrequencyHz, dissipationFactorPercent };
        }

        public string[] ReadControlState()
        {
            lock (stateLock)
            {
                return new[]
                {
                    positivePolarity ? "POSITIVE" : "NEGATIVE",
                    planMode ? "PLAN" : "CUSTOM",
                    advancedMode ? "ADVANCED" : "NORMAL",
                    automaticPressureEnabled ? "AUTOMATIC_PRESSURE" : "MANUAL_PRESSURE",
                    automaticRange ? "AUTO" : "MANUAL",
                    selectedRangeCode,
                    customVoltageModeCode,
                    customVoltageValue.ToString("0.0", CultureInfo.InvariantCulture),
                    CalculateTargetPeakVoltageKvNoLock().ToString("0.0", CultureInfo.InvariantCulture),
                    pressureStateCode,
                    selectedChannelCode,
                    selectedPhaseCode,
                    selectedPlanStepIndex.ToString(CultureInfo.InvariantCulture),
                    !IsHighVoltageFeedbackKnownNoLock() ? "HV_UNKNOWN" : IsHighVoltagePermissionNoLock() ? "HV_OPEN" : "HV_CLOSED",
                    waveformWindowCode
                };
            }
        }

        public void SetPressureState(string stateCode, double actualPeakKv)
        {
            string normalized = NormalizePressureState(stateCode);
            RunOnUiThread(delegate
            {
                if (normalized == "WAITING" || normalized == "FAULT") CancelAutomaticPressureSchedulingCore();
                lock (stateLock)
                {
                    pressureStateCode = normalized;
                    actualPeakVoltageKv = IsFinite(actualPeakKv) && actualPeakKv >= 0 ? actualPeakKv : Double.NaN;
                }
                UpdatePressureUi();
            });
        }

        public void RequestPressure()
        {
            if (PressureNavigationLocked || IsBusy) return;
            RunOnUiThread(delegate
            {
                if (PressureNavigationLocked || IsBusy) return;
                if (!AllowExplicitManualRangeRestart()) return;
                if (!ConfirmInterruptedPressureRestart()) return;
                CancelAutomaticPressureSchedulingCore();
                bool workflowChanged;
                lock (stateLock)
                {
                    if (IsBusy) return;
                    if (IsPressureRequestBlockedNoLock())
                    {
                        lastActionCode = "PRESSURE_BLOCKED";
                        lastPostProcessError = "自动保存、分析或超量程决策尚未完成";
                        return;
                    }
                    double requestedPeak = CalculateTargetPeakVoltageKvNoLock();
                    // Recheck at the command boundary: stale configurations or
                    // later U0 conversion changes must not bypass the editor.
                    if (!planMode && requestedPeak > 0 && requestedPeak < MinimumCustomPeakVoltageKv - 1e-12)
                    {
                        lastActionCode = "PRESSURE_BLOCKED";
                        lastPostProcessError = PressureUiText("非零电压最低峰值为 1 kV", "The minimum nonzero peak voltage is 1 kV.");
                        UpdatePressureUi();
                        return;
                    }
                    // A zero-voltage request is a non-energizing control state and
                    // remains available even before an HV limit has been configured.
                    // Positive voltage still requires the configured hard limit.
                    if (!IsFinite(requestedPeak) || requestedPeak < 0 ||
                        (requestedPeak > 0 && (maximumPeakVoltageKv <= 0 || requestedPeak > maximumPeakVoltageKv)))
                    {
                        lastActionCode = "PRESSURE_BLOCKED";
                        lastPostProcessError = "目标峰值超过设备限压或限压未配置 / Invalid peak voltage limit";
                        return;
                    }
                    if (!CanRequestVoltageNoLock(requestedPeak))
                    {
                        lastActionCode = "PRESSURE_BLOCKED_HV_NOT_READY";
                        lastPostProcessError = PressureUiText("急停未解除或状态尚未确认，暂不能开始试验", "Emergency stop active or status not confirmed; the test cannot start.");
                        UpdatePressureUi();
                        return;
                    }
                    if (!BeginAction()) return;
                    ResetFinishCurrentRound(); // An accepted explicit manual request starts a new round.
                    requestedTargetPeakVoltageKv = CalculateTargetPeakVoltageKvNoLock();
                    RememberIssuedPressurePlanNoLock();
                    ResetLocationCycleAcquisition();
                    cyclePressureAttempt = requestedTargetPeakVoltageKv > 0;
                    BeginLocationCycleAcquisition();
                    actualPeakVoltageKv = Double.NaN;
                    pressureRequestSequence++;
                    RecordPressureBatchRequestNoLock();
                    RecordOperatorPressureIntentNoLock();
                    lastActionCode = "PRESSURE";
                    workflowChanged = PreparePostProcessForPressureNoLock();
                }
                UpdatePressureUi();
                UpdatePostProcessUi();
                PublishAction("PRESSURE");
                if (workflowChanged) Raise(PostProcessStateChanged);
                Raise(PressureRequested);
            });
        }

        public void RequestDelete()
        {
            TryRequestDelete();
        }

        public void SetSelectedChannel(string channelCode)
        {
            string normalized = NormalizeChannel(channelCode);
            RunOnUiThread(delegate
            {
                bool changed;
                lock (stateLock)
                {
                    changed = !String.Equals(selectedChannelCode, normalized, StringComparison.Ordinal);
                    selectedChannelCode = normalized;
                    if (changed) channelChangedSequence++;
                }
                UpdateChannelUi();
                if (changed)
                {
                    Raise(ChannelChanged);
                    Raise(SpectrumChanged);
                }
            });
        }

        public void SetSelectedSpectrum(string spectrumCode)
        {
            SetSelectedChannel(spectrumCode);
        }

        public void SetSelectedPhase(string phaseCode)
        {
            string normalized = NormalizePhase(phaseCode);
            RunOnUiThread(delegate
            {
                if (PressureParametersLocked) return;
                bool changed;
                lock (stateLock)
                {
                    changed = !String.Equals(selectedPhaseCode, normalized, StringComparison.Ordinal);
                    selectedPhaseCode = normalized;
                    if (changed)
                    {
                        phaseChangedSequence++;
                        InvalidateDischargeCalibrationNoLock();
                        ResetFinishCurrentRound();
                        // A different phase starts a new operator-selected plan,
                        // never inherits the previous phase's issued batch.
                        int firstZero = Array.FindIndex(planVoltagesKv, value => value == 0d);
                        selectedPlanStepIndex = firstZero >= 0 ? firstZero : (planVoltagesKv.Length > 0 ? 0 : -1);
                        currentPlanRepetition = 1;
                        automaticPlanInterrupted = true;
                        issuedPlanStep = -1;
                        issuedPlanRepetition = 0;
                        issuedPlanProject = issuedPlanPhase = String.Empty;
                    }
                }
                if (changed) CancelAutomaticPressureSchedulingCore();
                UpdatePhaseUi();
                UpdateRangeUi();
                if (changed)
                {
                    SelectPlanGridRow(SelectedPlanStepIndex);
                    InvalidateNoiseReferenceContext();
                    RaiseVoltageSettingChanged();
                    Raise(PhaseChanged);
                }
            });
        }

        public void SetWaveformWindow(string windowCode)
        {
            string normalized = NormalizeWaveformWindow(windowCode);
            RunOnUiThread(delegate
            {
                bool changed;
                lock (stateLock)
                {
                    changed = !String.Equals(waveformWindowCode, normalized, StringComparison.Ordinal);
                    waveformWindowCode = normalized;
                    if (changed) waveformWindowChangedSequence++;
                }
                UpdateWaveformWindowUi();
                if (changed) Raise(WaveformWindowChanged);
            });
        }

        public void ClearDisplayData()
        {
            RunOnUiThread(delegate
            {
                // A host-owned DELETE operation legitimately clears the view
                // before its ACK. Never grant that exception to PRESSURE.
                if (PressureParametersLocked && (PressureNavigationLocked || !ActionPending || ActionCode != "DELETE")) return;
                CancelAutomaticPressureSchedulingCore();
                CloseFrequencyReminder();
                ResetFinishCurrentRound();
                metricFrequencyAvailable = metricCapacitanceAvailable = metricTanDeltaAvailable = false;
                rawMeasuredCapacitanceUf=Double.NaN;
                rawMeasuredTanDeltaPercent=Double.NaN;
                lastFrequencyReminderState = String.Empty;
                ResetNoiseReferenceView();
                ResetLocationCycleAcquisition();
                waveformView.ClearData();
                if (spectrumDashboardView != null) spectrumDashboardView.ClearData();
                lock (stateLock)
                {
                    lastVoltageKv = new double[0];
                    lastDischargePc = new double[0];
                    maxDischargePc = equivalentCapacitanceUf = oscillationFrequencyHz = dissipationFactorPercent = 0;
                    locationConfidencePercent = 0;
                    analysisStatus = "CLEARED";
                    lastActionCode = "CLEAR_DISPLAY";
                }
                UpdateMetricsUi();
                UpdateLocationFilterUi();
            });
        }

        public void SavePng(string filePath, int width, int height)
        {
            if (String.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("A file path is required.", "filePath");
            if (width < 800 || height < 400)
                throw new ArgumentOutOfRangeException("width", "PNG dimensions must be at least 800 x 400.");
            string directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            using (Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                RunOnUiThread(delegate
                {
                    Size original = Size;
                    try
                    {
                        Size = new Size(width, height);
                        PerformLayout();
                        DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    }
                    finally
                    {
                        Size = original;
                        PerformLayout();
                    }
                });
                // Encoding and file I/O intentionally stay outside the marshalled
                // UI section when LabVIEW calls this low-frequency export API.
                bitmap.Save(filePath, ImageFormat.Png);
            }
        }

        void AnalyzeCurrentWaveforms()
        {
            if(currentAcquisitionReferencePreview){ApplyReferencePreviewMetrics();return;}
            if(currentAcquisitionReferenceOnly){ApplyNoiseReferenceMetrics();return;}
            if(currentZeroVoltageAnalysis){ApplyZeroVoltageAnalysisMetrics();return;}
            double[] voltage;
            double[] discharge;
            double sampleRate;
            double dischargeRate;
            double inductance;
            lock (stateLock)
            {
                // Input arrays are cloned once on installation and never mutated;
                // retaining their immutable snapshot avoids two more LOH copies.
                voltage = lastVoltageKv;
                discharge = lastDischargePc;
                sampleRate = lastSampleRateHz;
                dischargeRate=lastPressureCapture!=null&&Object.ReferenceEquals(lastPressureCapture.ChargePc,discharge)?lastPressureCapture.Capture.PicoRateHz:sampleRate;
                inductance = inductanceH;
            }
            if (voltage.Length < 3 || discharge.Length == 0)
            {
                lock (stateLock) analysisStatus = "INSUFFICIENT_DATA";
                return;
            }
            try
            {
                PressureTestAnalysisResult result = PressureTestAnalysis.Analyze(
                    voltage, discharge, sampleRate, inductance, PdPolarity.Both,
                    new PressureTestAnalysisOptions { SkipDissipationFactor=!PressureMetricWindow.TanDeltaEligible(voltage,sampleRate,BaseU0VoltageKv) });
                lock (stateLock)
                {
                    analysisStatus = Convert.ToString(result.Status, CultureInfo.InvariantCulture);
                    lastError = String.Empty;
                }
                ApplyRealtimeMetrics(PressureMetricWindow.PeakAfterFirstCrossing(voltage,sampleRate,discharge,dischargeRate), result.EquivalentCapacitanceUf,
                    result.OscillationFrequencyHz, result.DissipationFactorPercent);
                lock (stateLock) analysisStatus = Convert.ToString(result.Status, CultureInfo.InvariantCulture);
            }
            catch (Exception exception)
            {
                lock (stateLock)
                {
                    analysisStatus = "ANALYSIS_FAILED";
                    lastError = exception.Message;
                }
            }
        }

        void RecalculatePlanVoltagesWhenDerived()
        {
            lock (stateLock)
            {
                for (int i = 0; i < planVoltagesKv.Length && i < planMultipliers.Length; i++)
                    planVoltagesKv[i] = planMultipliers[i] * baseU0VoltageKv;
            }
            RefreshPlanGrid();
        }

        double CalculateTargetPeakVoltageKv()
        {
            lock (stateLock) return CalculateTargetPeakVoltageKvNoLock();
        }

        double maximumPeakVoltageKv;
        const double MinimumCustomPeakVoltageKv = 1.0;
        string voltageLimitMessage = String.Empty;

        double CustomVoltagePeakFactorNoLock()
        {
            return customVoltageModeCode == "PEAK" ? 1.0 : customVoltageModeCode == "RMS"
                ? Math.Sqrt(2.0) : baseU0VoltageKv * Math.Sqrt(2.0);
        }

        double MinimumCustomVoltageValueNoLock()
        {
            return MinimumCustomPeakVoltageKv / CustomVoltagePeakFactorNoLock();
        }
        public double MaximumPeakVoltageKv
        {
            get { lock(stateLock) return maximumPeakVoltageKv; }
            set { RunOnUiThread(delegate { if(PressureParametersLocked)return;lock(stateLock) maximumPeakVoltageKv = IsFinite(value) && value > 0 ? value : 0; }); }
        }

        double CalculateTargetPeakVoltageKvNoLock()
        {
            if (planMode)
            {
                if (selectedPlanStepIndex < 0 || selectedPlanStepIndex >= planVoltagesKv.Length) return 0;
                double voltage = planVoltagesKv[selectedPlanStepIndex];
                return planVoltagesArePeak ? voltage : voltage * Math.Sqrt(2.0);
            }
            // Never round before the safety comparison or command snapshot.
            // Formatting to one decimal belongs only to UI text.
            return customVoltageValue * CustomVoltagePeakFactorNoLock();
        }

        int CurrentPlanRepetitionTotalNoLock()
        {
            if (!planMode || selectedPlanStepIndex < 0 || selectedPlanStepIndex >= planRepetitions.Length) return 1;
            return Math.Max(1, planRepetitions[selectedPlanStepIndex]);
        }

        void RaiseVoltageSettingChanged()
        {
            lock (stateLock) voltageSettingChangedSequence++;
            UpdateCustomVoltageUi();
            UpdatePressureUi();
            Raise(VoltageSettingChanged);
        }

        void Raise(EventHandler handler)
        {
            if (handler == null || Volatile.Read(ref disposeStarted) != 0) return;
            lock (eventDispatchLock)
            {
                pendingEventNotifications.Enqueue(handler);
                if (eventDispatchInProgress) return;
                eventDispatchInProgress = true;
            }

            Exception firstError = null;
            while (true)
            {
                EventHandler notification;
                lock (eventDispatchLock)
                {
                    if (pendingEventNotifications.Count == 0)
                    {
                        eventDispatchInProgress = false;
                        break;
                    }
                    notification = pendingEventNotifications.Dequeue();
                }

                foreach (EventHandler subscriber in notification.GetInvocationList())
                {
                    try { subscriber(this, EventArgs.Empty); }
                    catch (Exception exception)
                    {
                        // External subscribers must never prevent later subscribers
                        // or the next workflow notification from observing committed state.
                        Trace.TraceError("PressureTest event subscriber failed: {0}", exception);
                        if (firstError == null) firstError = exception;
                    }
                }
            }
            // Preserve the original public event contract: after all subscribers
            // and any re-entrant notifications observed the committed state, the
            // first subscriber failure is rethrown to the initiating caller.
            if (firstError != null) ExceptionDispatchInfo.Capture(firstError).Throw();
        }

        void RunOnUiThread(Action action)
        {
            if (action == null || Volatile.Read(ref disposeStarted) != 0 || IsDisposed) return;
            if (Thread.CurrentThread.ManagedThreadId == ownerThreadId)
            {
                action();
                return;
            }

            UiDispatchRequest request = new UiDispatchRequest(action, Volatile.Read(ref handleGeneration));
            QueueUiRequest(request);
            request.Wait();
            if (request.Error != null) ExceptionDispatchInfo.Capture(request.Error).Throw();
        }

        void QueueUiRequest(UiDispatchRequest request)
        {
            bool post = false;
            lock (uiDispatchLock)
            {
                if (Volatile.Read(ref disposeStarted) != 0 || IsDisposed)
                {
                    request.Complete(null);
                    return;
                }
                outstandingUiRequests.Add(request);
                if (!IsHandleCreated) pendingUiRequests.Enqueue(request);
                else post = true;
            }
            if (post) PostUiRequest(request);
        }

        void PostUiRequest(UiDispatchRequest request)
        {
            try
            {
                BeginInvoke(new MethodInvoker(delegate { ExecuteUiRequest(request); }));
            }
            catch (ObjectDisposedException)
            {
                CompleteUiRequest(request, null);
            }
            catch (InvalidOperationException exception)
            {
                if (Volatile.Read(ref disposeStarted) != 0 || IsDisposed) CompleteUiRequest(request, null);
                else CompleteUiRequest(request, exception);
            }
        }

        void ExecuteUiRequest(UiDispatchRequest request)
        {
            if (Interlocked.CompareExchange(ref request.State, UiDispatchRequest.Running,
                UiDispatchRequest.Pending) != UiDispatchRequest.Pending) return;
            if (Volatile.Read(ref disposeStarted) != 0 || IsDisposed)
            {
                CompleteUiRequest(request, null);
                return;
            }
            if (request.HandleGeneration != Volatile.Read(ref handleGeneration))
            {
                CompleteUiRequest(request,
                    new InvalidOperationException("The pressure-test control handle changed while dispatching a request."));
                return;
            }
            try { request.Action(); CompleteUiRequest(request, null); }
            catch (Exception exception) { CompleteUiRequest(request, exception); }
        }

        void CompleteUiRequest(UiDispatchRequest request, Exception error)
        {
            lock (uiDispatchLock) outstandingUiRequests.Remove(request);
            request.Complete(error);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Interlocked.Increment(ref handleGeneration);
            UiDispatchRequest[] pending;
            lock (uiDispatchLock)
            {
                pending = pendingUiRequests.ToArray();
                pendingUiRequests.Clear();
            }
            foreach (UiDispatchRequest request in pending)
            {
                request.HandleGeneration = Volatile.Read(ref handleGeneration);
                ExecuteUiRequest(request);
            }
            RefreshP04FeedbackForCreatedHandle();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            CancelPressureStartConfirmation();
            CancelAutomaticPressureSchedulingCore();
            CloseCaptureCooldown();
            Interlocked.Increment(ref handleGeneration);
            InvalidateP04FeedbackUiDispatch();
            UiDispatchRequest[] invalidated;
            lock (uiDispatchLock)
            {
                invalidated = outstandingUiRequests.ToArray();
                outstandingUiRequests.Clear();
                pendingUiRequests.Clear();
            }
            Exception error = Volatile.Read(ref disposeStarted) != 0
                ? null
                : new InvalidOperationException("The pressure-test control handle was destroyed while dispatching a request.");
            foreach (UiDispatchRequest request in invalidated) request.Complete(error);
            base.OnHandleDestroyed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref disposeStarted, 1) == 0)
            {
                CancelPressureStartConfirmation();
                CancelPressureNoiseInitialization();
                RequestHardwareStop();
                if (zeroContextTimer != null) { zeroContextTimer.Stop(); zeroContextTimer.Dispose(); zeroContextTimer = null; }
                DisposeP04Feedback();
                CancelAutomaticPressureSchedulingCore();
                CloseCaptureCooldown();
                CloseFrequencyReminder();
                CloseActionMailbox();
                actionDisabledRoots.Clear();
                Interlocked.Increment(ref handleGeneration);
                UiDispatchRequest[] pending;
                lock (uiDispatchLock)
                {
                    pending = outstandingUiRequests.ToArray();
                    pendingUiRequests.Clear();
                    outstandingUiRequests.Clear();
                }
                foreach (UiDispatchRequest request in pending) request.Complete(null);

                if (rangeMenu != null)
                {
                    rangeMenu.Close();
                    rangeMenu.Dispose();
                    rangeMenu = null;
                }
                ClearExternalEventHandlers();
            }
            base.Dispose(disposing);
        }

        void ClearExternalEventHandlers()
        {
            ActionPublished = null;
            PolarityChanged = null;
            ProgramModeChanged = null;
            VoltageSettingChanged = null;
            AdvancedModeChanged = null;
            AutomaticPressureChanged = null;
            PressurePreferencesChanged = null;
            RangeModeChanged = null;
            RangeChanged = null;
            PressureRequested = null;
            DeleteRequested = null;
            ChannelChanged = null;
            PhaseChanged = null;
            WaveformWindowChanged = null;
            RealtimeDataUpdated = null;
            LocationDataUpdated = null;
            LocationFilterChanged = null;
            SpectrumChanged = null;
            SpectrumViewChanged = null;
            EndpointReviewRequested = null;
            AutomaticPostProcessChanged = null;
            OverRangeReminderChanged = null;
            SaveRequested = null;
            AnalysisRequested = null;
            OverRangeDecisionRequested = null;
            RepressureRequested = null;
            PostProcessStateChanged = null;
        }

        static void CompactFiniteLocationData(ref double[] positions, ref double[] charges)
        {
            int count = Math.Min(positions.Length, charges.Length);
            int accepted = 0;
            for (int i = 0; i < count; i++)
            {
                if (!IsFinite(positions[i]) || !IsFinite(charges[i])) continue;
                positions[accepted] = positions[i];
                charges[accepted] = charges[i];
                accepted++;
            }
            if (positions.Length != accepted) Array.Resize(ref positions, accepted);
            if (charges.Length != accepted) Array.Resize(ref charges, accepted);
        }

        sealed class UiDispatchRequest
        {
            internal const int Pending = 0;
            internal const int Running = 1;
            internal const int CompletedState = 2;

            internal readonly Action Action;
            readonly object completionLock = new object();
            bool completed;
            internal Exception Error;
            internal int State;
            internal int HandleGeneration;

            internal UiDispatchRequest(Action action, int handleGeneration)
            {
                Action = action;
                HandleGeneration = handleGeneration;
            }

            internal void Complete(Exception error)
            {
                lock (completionLock)
                {
                    if (completed) return;
                    Error = error;
                    completed = true;
                    Interlocked.Exchange(ref State, CompletedState);
                    Monitor.PulseAll(completionLock);
                }
            }

            internal void Wait()
            {
                lock (completionLock)
                {
                    while (!completed) Monitor.Wait(completionLock);
                }
            }
        }

        static string NormalizeProgramMode(string value)
        {
            string text = (value ?? String.Empty).Trim().ToUpperInvariant();
            if (text == "PLAN" || text == "PLANNED" || text == "计划" || text == "计划加压") return "PLAN";
            if (text == "CUSTOM" || text == "自定义" || text == "自定义加压") return "CUSTOM";
            throw new ArgumentException("Expected PLAN or CUSTOM.", "value");
        }

        static string NormalizeVoltageMode(string value)
        {
            string text = (value ?? String.Empty).Trim().ToUpperInvariant();
            if (text == "U0" || text == "U0_MODE") return "U0";
            if (text == "RMS" || text == "EFFECTIVE" || text == "有效值") return "RMS";
            if (text == "PEAK" || text == "峰值") return "PEAK";
            throw new ArgumentException("Expected U0, RMS or PEAK.", "value");
        }

        static string NormalizeWaveformWindow(string value)
        {
            string text = (value ?? String.Empty).Trim().ToUpperInvariant();
            if (text == "ONE" || text == "1" || text == "1周期" || text == "ONE_CYCLE") return "ONE";
            if (text == "TWO" || text == "2" || text == "2周期" || text == "TWO_CYCLES") return "TWO";
            if (text == "MAX" || text == "MAXIMUM" || text == "LONGEST" || text == "最长" || text == "最长时间") return "MAXIMUM";
            throw new ArgumentException("Expected ONE, TWO or MAXIMUM.", "value");
        }

        static string NormalizeRange(string value)
        {
            string compact = (value ?? String.Empty).Replace(" ", String.Empty).Trim();
            for (int i = 0; i < RangeCodes.Length; i++)
                if (String.Equals(compact, RangeCodes[i], StringComparison.OrdinalIgnoreCase))
                    return RangeCodes[i];
            if (String.Equals(compact, "未校准", StringComparison.OrdinalIgnoreCase)||String.Equals(compact,"UNCALIBRATED",StringComparison.OrdinalIgnoreCase)) return "UNCALIBRATED";
            throw new ArgumentException("Expected explicit PICO:0 through PICO:10. Fixed pC labels cannot identify a hardware range.", "value");
        }

        static string NormalizePressureState(string value)
        {
            string text = (value ?? String.Empty).Trim().ToUpperInvariant();
            if (text == "WAITING" || text == "等待加压") return "WAITING";
            if (text == "STOPPED") return "STOPPED";
            if (text == "RUNNING" || text == "PRESSURIZING" || text == "正在加压") return "RUNNING";
            if (text == "RAMPING" || text == "升压中") return "RAMPING";
            if (text == "HOLDING" || text == "保持加压") return "HOLDING";
            if (text == "COMPLETE" || text == "COMPLETED" || text == "加压完成") return "COMPLETE";
            if (text == "FAULT" || text == "ERROR" || text == "加压异常") return "FAULT";
            throw new ArgumentException("Expected WAITING, RUNNING, RAMPING, HOLDING, COMPLETE or FAULT.", "value");
        }

        static string NormalizeChannel(string value)
        {
            string text = (value ?? String.Empty).Trim().ToUpperInvariant();
            if (text == "L-Q") return "Q-L";
            if (text == "L-N") return "N-L";
            if (text == "U-N") return "U-N-L";
            if (text == "TF" || text == "时频" || text == "TIME-FREQUENCY") return "F-T";
            if (text == "Q-L" || text == "N-L" || text == "Q-PHI" || text == "N-PHI" ||
                text == "F-T" || text == "U-Q-L" || text == "U-N-L" || text == "PRPD" || text == "L-PRPD") return text;
            if (text == "N-Q-U") return "n-Q-U";
            if (text == "N-N-U") return "n-N-U";
            throw new ArgumentException("Expected Q-L, N-L, Q-PHI, N-PHI, F-T, U-Q-L, U-N-L, n-Q-U, n-N-U, PRPD or L-PRPD.", "value");
        }

        static string NormalizePhase(string value)
        {
            string text = (value ?? String.Empty).Trim().ToUpperInvariant();
            if (text == "A" || text == "A相" || text == "L1") return "A";
            if (text == "B" || text == "B相" || text == "L2") return "B";
            if (text == "C" || text == "C相" || text == "L3") return "C";
            throw new ArgumentException("Expected A, B or C.", "value");
        }

        static string[] ReadProjectLines(string path)
        {
            var file=new FileInfo(path);
            if(!file.Exists||file.Length<1||file.Length>2*1024*1024)throw new InvalidDataException("Project metadata size is invalid.");
            byte[] bytes=File.ReadAllBytes(path);string text;
            if(bytes.Length>2*1024*1024)throw new InvalidDataException("Project metadata is too large.");
            if(bytes.Length>=2&&bytes[0]==255&&bytes[1]==254)text=new UnicodeEncoding(false,false,true).GetString(bytes,2,bytes.Length-2);
            else if(bytes.Length>=2&&bytes[0]==254&&bytes[1]==255)text=new UnicodeEncoding(true,false,true).GetString(bytes,2,bytes.Length-2);
            else {
                try{text=new UTF8Encoding(false,true).GetString(bytes).TrimStart('\uFEFF');}
                catch(DecoderFallbackException){text=Encoding.GetEncoding(936,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback).GetString(bytes);}
            }
            if(text.IndexOf('\0')>=0)throw new InvalidDataException("Project metadata encoding is invalid.");
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        static string ValueForKey(string[] lines, string key)
        {
            foreach (string raw in lines)
            {
                string line = raw == null ? String.Empty : raw.Trim();
                if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
                int colon = line.IndexOf('：');
                if (colon < 0) colon = line.IndexOf(':');
                return colon >= 0 ? line.Substring(colon + 1).Trim() : String.Empty;
            }
            return String.Empty;
        }

        static double[] ParseDoubles(string text)
        {
            List<double> values = new List<double>();
            foreach (string item in (text ?? String.Empty).Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
            {
                double value;
                if (Double.TryParse(item.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                    Double.TryParse(item.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value)) values.Add(value);
            }
            return values.ToArray();
        }

        static int[] ParseIntegers(string text)
        {
            List<int> values = new List<int>();
            foreach (string item in (text ?? String.Empty).Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int value;
                if (Int32.TryParse(item.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) values.Add(value);
            }
            return values.ToArray();
        }

        static bool TryParseFirstNumber(string text, out double value)
        {
            string filtered = new String((text ?? String.Empty).TakeWhile(character =>
                Char.IsDigit(character) || character == '.' || character == '-' || character == '+').ToArray());
            return Double.TryParse(filtered, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        static double CleanMetric(double value) { return IsFinite(value) ? value : 0; }
        static double RoundOne(double value) { return Math.Round(value, 1, MidpointRounding.AwayFromZero); }
        static bool IsFinite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
    }
}
