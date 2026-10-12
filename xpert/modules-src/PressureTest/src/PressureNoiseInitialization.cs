using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xiongxin.ZeroBench;

namespace HVX.Controls
{
    // A read-only initialization request is not a PRESSURE mailbox action and
    // cannot create output authorization, start a plan or accept a PD round.
    public sealed partial class PressureTestControl
    {
        volatile bool pressureNoiseInitializing;
        bool pressureNoiseInitializationRequired, pressureNoiseInitializationReady;
        string pressureNoiseInitializationError = "";
        string pressureNoiseInitializationStages = "";
        CancellationTokenSource pressureNoiseInitializationCancellation;
        long pressureNoiseInitializationGeneration;
        string pressureInitialRangeProject="", pressureInitialRangeProfile="";
        int pressureInitialRange=-1;
        readonly Dictionary<string,string> pressureInitialRangeCalibrations=new Dictionary<string,string>();
        [Browsable(false)] public bool IsPressureNoiseInitializing { get { return pressureNoiseInitializing; } }
        [Browsable(false)] public bool PressureNoiseInitializationReady { get { return !pressureNoiseInitializationRequired || pressureNoiseInitializationReady; } }
        [Browsable(false)] public string PressureNoiseInitializationError { get { return pressureNoiseInitializationError; } }
        [Browsable(false)] public string PressureNoiseInitializationStages { get { return pressureNoiseInitializationStages; } }
        bool PressureNoiseInitializationBlocksStart { get { return pressureNoiseInitializing || pressureNoiseInitializationRequired && !pressureNoiseInitializationReady; } }

        void UpdatePressureNoiseInitializationUi()
        {
            if (pressureActionButton == null || !pressureNoiseInitializationRequired || pressureNoiseInitializationReady) return;
            string text = pressureNoiseInitializing ? PressureUiText("初始化中", "Initializing") : PressureUiText("初始化未完成", "Initialization incomplete");
            if (pressureActionButton.Text != text) pressureActionButton.Text = text;
            if (pressureActionButton.Enabled) pressureActionButton.Enabled = false;
            pressureActionButton.AccessibleDescription = pressureNoiseInitializing ?
                PressureUiText("正在准备量程；不会启动加压。", "Preparing the input range; pressure will not start.") : pressureNoiseInitializationError;
        }
        void CancelPressureNoiseInitialization()
        {
            var source = pressureNoiseInitializationCancellation;
            if (source != null) try { source.Cancel(); } catch (ObjectDisposedException) { }
        }

        public Task<bool> InitializePressureNoiseAsync(string settingsXml, Func<string,CancellationToken,Task<string>> inputCapture, CancellationToken cancellation)
        { return InitializePressureNoiseWithHandoffAsync(settingsXml,inputCapture,token=>Task.CompletedTask,cancellation); }

        public async Task<bool> InitializePressureNoiseWithHandoffAsync(string settingsXml, Func<string,CancellationToken,Task<string>> inputCapture,
            Func<CancellationToken,Task> completeInputOwner, CancellationToken cancellation)
        {
            if (inputCapture == null) throw new ArgumentNullException("inputCapture");
            if (completeInputOwner == null) throw new ArgumentNullException("completeInputOwner");
            var settings = PressureZeroVoltageCapture.ReadSettings(settingsXml);
            if (settings.CaptureDurationMs != 20 || settings.AutomaticSharedTrigger || settings.TargetVoltageV != 0 || settings.PicoCoupling != 0)
                throw new ArgumentException("Initialization requires explicit 20 ms AC input-only requests.");
            var profileSettings=PressureZeroVoltageCapture.Clone(settings);
            profileSettings.RangeIndex=PressureNoiseInitializationPolicy.MaximumRangeIndex;
            profileSettings.PdCalibrationPcPerV=1;
            string profile=PressureZeroVoltageCapture.SettingsXml(profileSettings);
            string project = null, phase = null, source = null, sourceHash = null;
            double coefficient = Double.NaN;
            long generation = 0;
            int selectedBeforeInitialization = -1;
            CancellationTokenSource owner = null;
            RunOnUiThread(delegate {
                if (IsDisposed || Disposing || !Visible || !IsHandleCreated || SimulationMode || pressureNoiseInitializing || IsBusy || HasActiveHardwareSession || !IsDischargeCalibrated)
                    throw new InvalidOperationException("The input page is not ready for read-only initialization.");
                if (!Directory.Exists(ProjectPath) || settings.PdCalibrationPcPerV != calibrationPcPerVolt || settings.RangeIndex != SelectedPicoRangeCode)
                    throw new InvalidOperationException("Initialization does not match the current project, calibration and range.");
                project = ProjectPath; phase = SelectedPhaseCode; source = calibrationSource; sourceHash = calibrationSourceHash; coefficient = calibrationPcPerVolt;
                selectedBeforeInitialization = SelectedPicoRangeCode;
                owner = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                pressureNoiseInitializationCancellation = owner;
                generation = ++pressureNoiseInitializationGeneration;
                pressureNoiseInitializationRequired = pressureNoiseInitializing = true;
                pressureNoiseInitializationReady = false; pressureNoiseInitializationError = "";
                CancelAutomaticPressureSchedulingCore();
                UpdateActionBusyUi(); UpdatePressureNoiseInitializationUi();
            });
            if (owner == null) return false;
            EventHandler cancelHidden = (s,e) => { if (!Visible) CancelPressureNoiseInitialization(); };
            EventHandler cancelHandle = (s,e) => CancelPressureNoiseInitialization();
            VisibleChanged += cancelHidden; HandleDestroyed += cancelHandle;
            bool success = false;
            var timer = Stopwatch.StartNew();
            var stage = Stopwatch.StartNew();
            long inputMs=0,readMs=0,rangeMs=0,evidenceMs=0;
            int requestedSegments=0, confirmedRange=-1;
            bool reused=false;
            Action requireCurrent = delegate {
                owner.Token.ThrowIfCancellationRequested();
                bool current = false;
                RunOnUiThread(delegate {
                    current = !IsDisposed && !Disposing && Visible && IsHandleCreated && pressureNoiseInitializing &&
                        generation == pressureNoiseInitializationGeneration && Object.ReferenceEquals(owner, pressureNoiseInitializationCancellation) &&
                        ProjectPath == project && SelectedPhaseCode == phase && IsDischargeCalibrated && !SimulationMode &&
                        calibrationPcPerVolt == coefficient && calibrationSource == source && calibrationSourceHash == sourceHash && SelectedPicoRangeCode==selectedBeforeInitialization;
                });
                if (!current) throw new OperationCanceledException("The initialization input owner or measurement context changed.");
            };
            try {
                // One real +/-20 V, 20 ms probe, never a pressure round. Phase
                // changes reuse this physical starting range without inventing
                // a phase-specific noise reference or doing another capture.
                requireCurrent();
                string knownHash;
                reused=pressureInitialRange>=0 && pressureInitialRangeProject==project && pressureInitialRangeProfile==profile &&
                    (!pressureInitialRangeCalibrations.TryGetValue(phase,out knownHash)||knownHash==sourceHash);
                if(reused) confirmedRange=Math.Max(pressureInitialRange,PressureChargeRangeFloor.MinimumIndex(coefficient));
                else {
                    ClearPressureInitialRangeCache();
                    settings.RangeIndex = PressureNoiseInitializationPolicy.MaximumRangeIndex;
                    requireCurrent(); stage.Restart();
                    DateTime started = DateTime.UtcNow;
                    requestedSegments++;
                    string returned = await inputCapture(PressureZeroVoltageCapture.SettingsXml(settings),owner.Token).ConfigureAwait(false);
                    inputMs+=stage.ElapsedMilliseconds;stage.Restart();
                    requireCurrent();
                    string directory = PressureZeroVoltageCapture.CaptureDirectory(project,returned);
                    var result = await Task.Run(() => PressureZeroVoltageCapture.ReadInitialRange(directory,settings,started,owner.Token),owner.Token).ConfigureAwait(false);
                    readMs+=stage.ElapsedMilliseconds;stage.Restart();
                    requireCurrent();
                    // WHOLE actual segment, before DC removal/filtering. Keep
                    // every attempted range's native clipping/overflow evidence.
                    var quality = LocationRangeAdvisor.Evaluate(result.Capture.PdAdc,0,result.Capture.PdAdc.Length,result.Capture.MaximumAdc,
                        result.Capture.PicoFullScaleV,settings.RangeIndex,0,coefficient,result.Capture.Overflow);
                    result.OriginalRangeQuality = result.Quality; result.Quality = quality;
                    rangeMs+=stage.ElapsedMilliseconds;stage.Restart();
                    PressureZeroVoltageCapture.WriteEvidence(result,"INITIALIZATION-"+generation+"-1",phase,source,null);
                    evidenceMs+=stage.ElapsedMilliseconds;
                    confirmedRange=PressureNoiseInitializationPolicy.StartingRange(quality,coefficient);
                }
                if (confirmedRange<0 || confirmedRange>PressureNoiseInitializationPolicy.MaximumRangeIndex) throw new PressureNoiseInitializationException();
                requireCurrent();
                // Do not enable pressure while the input-only owner is still
                // being released. The host verifies real cleanup before it
                // requests the separate normal idle monitor; failure stays
                // unready and never commits an initial range or starts HV.
                await completeInputOwner(owner.Token).ConfigureAwait(false);
                requireCurrent();
                RunOnUiThread(delegate {
                    owner.Token.ThrowIfCancellationRequested();
                    if (generation != pressureNoiseInitializationGeneration || !Object.ReferenceEquals(owner,pressureNoiseInitializationCancellation)) return;
                    // Commit only after real input-owner handoff, never on a
                    // predicted range alone while that owner is still pending.
                    lock(stateLock) {
                        selectedRangeCode = RangeCodes[confirmedRange]; rangeChangedSequence++;
                    }
                    if(!reused) {pressureInitialRange=confirmedRange;pressureInitialRangeProject=project;pressureInitialRangeProfile=profile;}
                    pressureInitialRangeCalibrations[phase]=sourceHash;
                    pressureNoiseInitializationReady = success = true;
                    pressureNoiseInitializationError = "";
                    UpdateRangeUi(); Raise(RangeChanged);
                });
            } catch(Exception error) {
                if (error is OutOfMemoryException) throw;
                RunOnUiThread(delegate { if (generation == pressureNoiseInitializationGeneration) {ClearPressureInitialRangeCache();pressureNoiseInitializationError = error.Message;} });
                throw;
            } finally {
                timer.Stop();
                string stages="requestedSegments="+requestedSegments+"; requestedMs=20; firstRangeV=20; initialRangeIndex="+confirmedRange+"; reusedInitialRange="+reused+"; elapsedMs="+timer.ElapsedMilliseconds+"; inputWaitAndCaptureMs="+inputMs+
                    "; rawReadAndHashMs="+readMs+"; fullRangeScanMs="+rangeMs+"; evidenceMs="+evidenceMs+"; displayPrepared=False; completed="+success;
                RunOnUiThread(delegate {
                    VisibleChanged -= cancelHidden; HandleDestroyed -= cancelHandle;
                    if (generation == pressureNoiseInitializationGeneration && Object.ReferenceEquals(owner,pressureNoiseInitializationCancellation)) {
                        pressureNoiseInitializing = false; pressureNoiseInitializationCancellation = null;
                        pressureNoiseInitializationStages=stages;
                        UpdateActionBusyUi(); UpdatePressureUi(); UpdatePressureNoiseInitializationUi();
                    }
                });
                owner.Dispose();
                Trace.TraceInformation("PressureNoiseInitialization: "+stages);
            }
            return success;
        }
        void ClearPressureInitialRangeCache()
        { pressureInitialRange=-1;pressureInitialRangeProject=pressureInitialRangeProfile="";pressureInitialRangeCalibrations.Clear(); }
    }

    internal static class PressureNoiseInitializationPolicy
    {
        internal static int MaximumRangeIndex { get { return BenchSettings.Ranges.Length-1; } }
        static int Validate(LocationRangeResult quality,double coefficient)
        {
            int floor=PressureChargeRangeFloor.MinimumIndex(coefficient);
            if (floor<0 || quality==null || !quality.WindowValid || quality.LocationSampleCount<1 ||
                quality.LocationSampleCount!=quality.FullSampleCount || quality.WindowShortened ||
                Double.IsNaN(quality.FullPeakVolts) || Double.IsInfinity(quality.FullPeakVolts) || quality.FullPeakVolts<0 ||
                quality.RecommendedIndex<0 || quality.RecommendedIndex>MaximumRangeIndex)
                throw new PressureNoiseInitializationException();
            return floor;
        }
        internal static int StartingRange(LocationRangeResult quality,double coefficient)
        {
            int floor=Validate(quality,coefficient);
            if (
                quality.FullClippedSamples != 0 || quality.DriverOverflow || quality.DriverOverflowUncertain || quality.RecommendationUncertain ||
                quality.MaximumRangeInsufficient || quality.LocationHighOccupancy || quality.AtMaximumAndClipped)
                throw new PressureNoiseInitializationException();
            return Math.Max(floor,quality.RecommendedIndex);
        }
    }

    // A distinct failure category keeps native capture-quality failures out of
    // generic "CAL/settings invalid" notifications. Technical evidence is on disk.
    internal sealed class PressureNoiseInitializationException : Exception
    {
        internal PressureNoiseInitializationException() : base("信号检查未通过。请检查接线和输入信号后重新进入加压测试；若仍失败，请联系维护人员。") { }
    }
}
