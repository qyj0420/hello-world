using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        private bool hostAcquisitionActive;
        private bool hardwareCleanupInProgress;
        private bool hostConnectionUnavailable;
        private bool calibrationEntryCheckActive;
        private Task calibrationEntryCheckTask;
        private Form calibrationEntryNotice;
        private Xiongxin.Calibration.CalibrationForm activeAcquisition;
        private long hardwareSessionGeneration;
        private readonly string[] phaseGeometryNotices = new string[4];
        public int CalibrationAverageCount {get;set;} = 16;
        public Func<string[]> CalibrationHardwareIdentityProvider { get; set; }
        public Action<string> CalibrationEntryDiagnosticSink { get; set; }
        public bool? CalibrationEntryEmergencySignalHigh { get; private set; }
        internal Action<string> ShowCalibrationEntryNoticeForTests { get; set; }
        internal Func<Task<bool?>> ReadCalibrationEntryEmergencyForTests { get; set; }
        // Called by the host on actual page entry, never by resize/restore.
        public void NotifyCalibrationPageEntered()
        {
            if(SimulationMode || IsDisposed || Disposing)return;
            RunOnUiThread(delegate {
                ObserveCalibrationEntryTask(NotifyCalibrationPageEnteredAsync());
            });
        }
        // The host holds its environment handoff lease until this task really
        // completes. The legacy notification above remains source-compatible.
        public Task NotifyCalibrationPageEnteredAsync()
        {
            if(Environment.CurrentManagedThreadId!=ownerThreadId)
                throw new InvalidOperationException("Calibration entry must be requested on the UI thread.");
            if(SimulationMode || IsDisposed || Disposing)return Task.CompletedTask;
            if(calibrationEntryCheckActive)return calibrationEntryCheckTask ?? Task.CompletedTask;
            if(HardwareOperationBusy || hostConnectionUnavailable)return Task.CompletedTask;
            if(calibrationEntryNotice!=null&&!calibrationEntryNotice.IsDisposed)calibrationEntryNotice.Close();
            calibrationEntryCheckTask=CheckCalibrationEntryEmergencyAsync();
            return calibrationEntryCheckTask;
        }
        private async void ObserveCalibrationEntryTask(Task entry)
        {
            try {await entry;}
            catch(Exception error) {WriteCalibrationDiagnostic("Calibration entry did not complete: "+error);}
        }
        private async Task CheckCalibrationEntryEmergencyAsync()
        {
            calibrationEntryCheckActive=true;
            CalibrationEntryEmergencySignalHigh=null;
            Invalidate();
            var acquisition=activeAcquisition;
            long generation=checked(++hardwareSessionGeneration);
            try
            {
                bool? high;
                if(ReadCalibrationEntryEmergencyForTests!=null)
                    high=await ReadCalibrationEntryEmergencyForTests();
                else
                {
                    acquisition=activeAcquisition=acquisition ?? new Xiongxin.Calibration.CalibrationForm(null,true);
                    acquisition.HostHardwareIdentityProvider=CalibrationHardwareIdentityProvider;
                    acquisition.HostDialogOwner=FindForm();
                    acquisition.HostTranslateUi=text=>Xiongxin.OWTS.Localization.UiLanguage.BuiltIn(LanguageIndex,text);
                    high=await acquisition.HostCheckEmergencyOnEntryAsync();
                }
                if(generation!=hardwareSessionGeneration || SimulationMode || IsDisposed || Disposing || hostConnectionUnavailable)return;
                CalibrationEntryEmergencySignalHigh=high;
                // External calibration has its own input-only lifecycle. Record the
                // actual DI level, but neither HIGH nor LOW grants any HV permission
                // or requires an output-mode safety transition before sampling.
                string notice=high==true?CalibrationText(
                    "当前急停处于解除状态。校准不会加压；仍须按现场规程确认高压隔离与试品放电，不可据此接近试品。",
                    "The emergency stop is released. Calibration does not apply voltage. Follow site procedures to verify high-voltage isolation and discharge before approaching the test object."):high.HasValue?String.Empty:CalibrationEntryUnknownNotice;
                if(!high.HasValue)WriteCalibrationDiagnostic("Calibration entry emergency state is unknown; the reader returned no observed input level.");
                if(notice.Length>0)ShowCalibrationEntryNotice(notice);
            }
            catch(Exception error)
            {
                CalibrationEntryEmergencySignalHigh=null;
                string detail="Calibration entry emergency state is unknown; no capture permission was granted. "+error;
                WriteCalibrationDiagnostic(detail);
                if(generation==hardwareSessionGeneration&&!IsDisposed&&!Disposing&&!SimulationMode)
                    ShowCalibrationEntryNotice(CalibrationEntryUnknownNotice);
            }
            finally
            {
                calibrationEntryCheckActive=false;
                RetainOrDisposeAcquisition(acquisition);
                if(!IsDisposed&&!Disposing)Invalidate();
            }
        }
        private string CalibrationEntryUnknownNotice {get{return CalibrationText(
            "急停状态未确认，请检查设备连接后重试。当前未取得有效急停状态。",
            "The emergency-stop state is not confirmed. Check the device connection and retry; no valid input state was obtained.");}}
        private void WriteCalibrationDiagnostic(string detail)
        {
            try {if(CalibrationEntryDiagnosticSink!=null)CalibrationEntryDiagnosticSink(detail);else System.Diagnostics.Trace.WriteLine(detail);}
            catch(Exception logError){System.Diagnostics.Trace.WriteLine("Calibration diagnostic: "+logError);}
        }
        // A modeless reminder never requires LOW or grants permission to energize.
        // It closes with this page, and is not reissued by resize/restore.
        private void ShowCalibrationEntryNotice(string text)
        {
            if(ShowCalibrationEntryNoticeForTests!=null){ShowCalibrationEntryNoticeForTests(text);return;}
            if(calibrationEntryNotice!=null&&!calibrationEntryNotice.IsDisposed)calibrationEntryNotice.Close();
            var owner=FindForm();
            var notice=new Form {Text=CalibrationText("急停状态提醒","Emergency-stop state"),
                StartPosition=FormStartPosition.CenterParent,ShowInTaskbar=false,MinimizeBox=false,MaximizeBox=false,
                AutoScaleMode=AutoScaleMode.Dpi,ClientSize=new System.Drawing.Size(480,160),Font=Font};
            if(owner!=null)notice.Icon=owner.Icon;
            var body=new TextBox {Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,
                Text=text,BorderStyle=BorderStyle.None,BackColor=System.Drawing.SystemColors.Control};
            var close=new Button {Dock=DockStyle.Bottom,Height=36,Text=CalibrationText("知道了","Understood")};
            close.Click+=(s,e)=>notice.Close();notice.Controls.Add(body);notice.Controls.Add(close);notice.Padding=new Padding(18);
            calibrationEntryNotice=notice;
            EventHandler pageHidden=null;pageHidden=(s,e)=>{if(!Visible&&!notice.IsDisposed)notice.Close();};VisibleChanged+=pageHidden;
            EventHandler pageDisposed=null;pageDisposed=(s,e)=>{if(!notice.IsDisposed)notice.Dispose();};Disposed+=pageDisposed;
            notice.FormClosed+=(s,e)=>{VisibleChanged-=pageHidden;Disposed-=pageDisposed;if(Object.ReferenceEquals(calibrationEntryNotice,notice))calibrationEntryNotice=null;};
            if(owner!=null)notice.Show(owner);else notice.Show();
        }
        public bool HasActiveHardwareSession { get { return shortCableWriteInProgress || calibrationEntryCheckActive || hostAcquisitionActive || hardwareCleanupInProgress || (activeAcquisition != null && activeAcquisition.HasActiveHardwareSession); } }
        public bool HasReusableHardwareSession { get { return !calibrationEntryCheckActive && !hostAcquisitionActive && !hardwareCleanupInProgress && activeAcquisition!=null && activeAcquisition.HostSessionReusable; } }
        public bool CanReleaseHardwareSession { get { return !shortCableWriteInProgress && !calibrationEntryCheckActive && !hostAcquisitionActive && !hardwareCleanupInProgress; } }
        private bool HardwareOperationBusy { get { return shortCableWriteInProgress || calibrationEntryCheckActive || hostAcquisitionActive || hardwareCleanupInProgress || HasPendingHardwareCleanup; } }
        private bool HasPendingHardwareCleanup { get { return !calibrationEntryCheckActive && !hostAcquisitionActive && activeAcquisition != null && activeAcquisition.HasOwnedHardwareResources && !activeAcquisition.HostSessionReusable; } }
        private void RetainOrDisposeAcquisition(Xiongxin.Calibration.CalibrationForm acquisition)
        {
            if (acquisition == null || acquisition.HasActiveHardwareSession) return;
            if (acquisition.HostRequiresConnectionRecheck) hostConnectionUnavailable = true;
            if (Object.ReferenceEquals(activeAcquisition, acquisition)) activeAcquisition = null;
            acquisition.Dispose();
        }
        private bool IsCurrentHardwareAcquisition(Xiongxin.Calibration.CalibrationForm acquisition, long generation)
        {
            return !IsDisposed && !Disposing && !acquisition.IsDisposed &&
                generation == hardwareSessionGeneration && Object.ReferenceEquals(activeAcquisition, acquisition);
        }
        public async Task<bool> RetryHardwareCleanupAsync()
        {
            if (shortCableWriteInProgress || calibrationEntryCheckActive || hostAcquisitionActive || hardwareCleanupInProgress) return false;
            if (!HasPendingHardwareCleanup) return true;
            hardwareCleanupInProgress = true;
            Invalidate();
            try
            {
                var acquisition = activeAcquisition;
                await acquisition.HostReleaseAsync();
                RetainOrDisposeAcquisition(acquisition);
                return activeAcquisition == null;
            }
            finally
            {
                hardwareCleanupInProgress = false;
                if (!HasPendingHardwareCleanup) calibrationStopRequested = false;
                if (!IsDisposed && !Disposing) Invalidate();
            }
        }
        public async Task<bool> ReleaseHardwareSessionAsync()
        {
            if(shortCableWriteInProgress || calibrationEntryCheckActive || hostAcquisitionActive || hardwareCleanupInProgress)return false;
            if(activeAcquisition==null)return true;
            hardwareCleanupInProgress=true;checked{hardwareSessionGeneration++;}Invalidate();
            try
            {
                var acquisition=activeAcquisition;
                await acquisition.HostReleaseAsync();
                RetainOrDisposeAcquisition(acquisition);
                return activeAcquisition==null;
            }
            finally {hardwareCleanupInProgress=false;if(!HasPendingHardwareCleanup)calibrationStopRequested=false;if(!IsDisposed&&!Disposing)Invalidate();}
        }
        public bool RequestHardwareStop()
        {
            hostConnectionUnavailable = true;
            if (!HasActiveHardwareSession || activeAcquisition == null) return false;
            bool releaseIdle=HasReusableHardwareSession;
            calibrationStopRequested = true;
            activeAcquisition.HostCancel();
            Invalidate();
            if(releaseIdle)ReleaseStoppedIdleHardware();
            return true; // A stop request is not proof that release has completed.
        }
        private async void ReleaseStoppedIdleHardware()
        {
            try {await ReleaseHardwareSessionAsync();}
            catch(Exception error){System.Diagnostics.Debug.WriteLine("Calibration release retained: "+error);}
        }

        private void ApplyProjectGeometryDefaults(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
            try
            {
                for (int phase = -1; phase < 3; phase++)
                {
                    var defaults = Xiongxin.Calibration.ProjectDefaults.Load(path,phase<0?"ALL":((char)('A'+phase)).ToString());
                    if(defaults.CapacitiveLoad) {phaseGeometryNotices[phase+1]=defaults.Notice;continue;}
                    phaseGeometryNotices[phase+1]="材料："+defaults.Material+"；"+(defaults.VelocityMPerUs>0?defaults.VelocitySource:"无可靠波速，当前输入必须由操作员核对")+"。"+defaults.Notice;
                    persistedCableLengths[phase+1]=defaults.LengthM;
                    SetPhaseParameters(phase, defaults.LengthM, defaults.VelocityMPerUs>0?defaults.VelocityMPerUs:GetPhaseVelocityMPerUs(phase),
                        GetPhaseShortCable(phase), GetPhaseWideBand(phase));
                    foreach(string level in LevelCodes)RememberLiveLevelParameters(phase,level);
                }
            }
            catch (IOException ex) {
                WriteCalibrationDiagnostic("Project geometry could not be read: "+ex);
                for(int i=0;i<phaseGeometryNotices.Length;i++)phaseGeometryNotices[i]=CalibrationText("请核对项目电缆长度与材料波速后重试。","Review the project cable length and material wave velocity, then retry.");
            }
        }

        private async void StartHardwareAcquisitionOnOriginalPage(object sender, CalibrationRequestEventArgs request)
        {
            // Simulation remains owned by the existing Task101 bridge; only real
            // collection uses the SDK worker behind this unchanged page.
            if (SimulationMode || HardwareOperationBusy || IsDisposed || Disposing) return;
            if (hostConnectionUnavailable)
            {
                MessageBox.Show(FindForm(), CalibrationText("设备连接未就绪，请返回主页检查后重试。当前数据保留。", "The device connection is not ready. Return to the home page, check the connection and retry. Current data is retained."),
                    CalibrationText("校准采集", "Calibration acquisition"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (request == null || String.IsNullOrWhiteSpace(ProjectPath) || !Directory.Exists(ProjectPath))
            {
                MessageBox.Show(FindForm(), CalibrationText("请先载入有效项目。", "Load a valid project first."),
                    CalibrationText("校准采集", "Calibration acquisition"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string[] levels;
            try { levels = ResolveHardwareLevelCodes(request.LevelCode, request.AllLevels); }
            catch (ArgumentException)
            {
                MessageBox.Show(FindForm(), CalibrationText("无法识别该校准量，请选择有效档位。", "Select a valid calibration level."), CalibrationText("校准采集", "Calibration acquisition"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            calibrationStopRequested = false;
            hostAcquisitionActive = true;
            try
            {
                // The calibrator output is not software-controlled. Prompt for each
                // physical charge setting; never label one pulse as multiple charges.
                await RunHardwareAcquisitionLevels(
                    levels,
                    level => AcquireHardwareLevel(request, level));
            }
            catch (Exception error)
            {
                WriteCalibrationDiagnostic("Calibration acquisition did not complete: "+error);
                if (!IsDisposed && !Disposing)
                    MessageBox.Show(FindForm(), HasPendingHardwareCleanup
                        ?CalibrationText("设备释放未完成，请检查设备状态后重试。", "Device release is incomplete. Check the device state and retry.")
                        :CalibrationText("校准未完成，请检查设备连接和采集设置后重试。", "Calibration is incomplete. Check the device connection and acquisition settings, then retry."),
                        CalibrationText("校准采集", "Calibration acquisition"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { hostAcquisitionActive = false;RetainOrDisposeAcquisition(activeAcquisition);if(!HasPendingHardwareCleanup)calibrationStopRequested = false;if(!IsDisposed&&!Disposing)Invalidate(); }
        }

        private bool CanContinueHardwareAcquisition()
        { return !hostConnectionUnavailable && !calibrationStopRequested && !IsDisposed && !Disposing; }

        private async Task RunHardwareAcquisitionLevels(System.Collections.Generic.IEnumerable<string> levels,
            Func<string, Task<bool>> acquireLevel)
        {
            if (!CanContinueHardwareAcquisition()) return;
            foreach (string level in levels)
            {
                if (!CanContinueHardwareAcquisition()) break;
                bool completed = await acquireLevel(level);
                // A valid capture may already have been published while releasing
                // its device. Keep that data, but a stop arriving during release
                // must remain latched before another coordinator can clear it.
                if (!CanContinueHardwareAcquisition() || !completed) break;
            }
        }

        private async Task<bool> AcquireHardwareLevel(CalibrationRequestEventArgs request, string level)
        {
                if (!CanContinueHardwareAcquisition()) return false;
                double chargePc;
                if(!TryChargePc(level,out chargePc))throw new InvalidOperationException("校准档位无效。");
                selectedLevelCode=level;Invalidate();
                var acquisition = activeAcquisition ?? new Xiongxin.Calibration.CalibrationForm(null, true);
                Action<double[],double,double> wave=null,noise=null;
                Action<double,double,double> metrics=null;
                Action<Xiongxin.Calibration.CalibrationCaptureResult> completed=null;
                try
                {
                    activeAcquisition=acquisition;
                    acquisition.HostKeepSessionOpen=true;
                    acquisition.HostHardwareIdentityProvider=CalibrationHardwareIdentityProvider;
                    long generation=checked(++hardwareSessionGeneration);
                    acquisition.HostDialogOwner = FindForm();
                    acquisition.HostTranslateUi=text=>Xiongxin.OWTS.Localization.UiLanguage.BuiltIn(LanguageIndex,text);
                    acquisition.HostAverageCount=CalibrationAverageCount;
                    acquisition.HostScreeningThresholdV=ManualNoiseThreshold(request.PhaseIndex);
                    acquisition.HostAutomaticThresholdResolved=value=>{if(IsCurrentHardwareAcquisition(acquisition,generation)){automaticNoiseThresholds[request.PhaseIndex]=value;Invalidate();}};
                    acquisition.HostAcquisitionMode=AcquisitionMode;
                    acquisition.HostDistanceMode=level=="TDR";
                    acquisition.HostPhaseCode=PhaseCodeFromIndex(request.PhaseIndex);
                    acquisition.HostLevelCode=level;
                    acquisition.HostCapacitiveLoad=IsCapacitiveLoad;
                    acquisition.HostGeometryDescription=phaseGeometryNotices[request.PhaseIndex+1];
                    var templates=noiseTemplates;double templateTolerance=NoiseSimilarityTolerancePercent;
                    acquisition.HostPulseExcluded=(signal,peak,rate)=>HVX.SignalProcessing.NoisePulseTemplates.Matches(templates,signal,peak,rate,templateTolerance);
                    acquisition.LoadProject(ProjectPath, false);
                    wave = (samples, x0, dx) =>
                    {
                        if (IsCurrentHardwareAcquisition(acquisition,generation)) {
                            compactQualityTab=false;
                            SetPhaseWaveform(request.PhaseIndex, level, samples, x0, dx);
                        }
                    };
                    noise = (samples, x0, dx) =>
                    {
                        if (IsCurrentHardwareAcquisition(acquisition,generation)) {
                            compactQualityTab=true;
                            SetPhaseAcquiredNoiseWaveform(request.PhaseIndex, samples, x0, dx);
                        }
                    };
                    metrics = (snrDb, peakV, noiseV) =>
                    {
                        if (IsCurrentHardwareAcquisition(acquisition,generation))
                            SetSignalMetrics(snrDb, peakV, noiseV);
                    };
                    completed = result =>
                    {
                        if (IsCurrentHardwareAcquisition(acquisition,generation) && CanContinueHardwareAcquisition()) {
                            lock(stateLock) {
                                GetPhaseStateNoLock(request.PhaseIndex).ShortCable=result.ShortCable;
                                RememberLiveLevelParameters(request.PhaseIndex,level);
                            }
                            ReceiveLiveCapture(request.PhaseIndex,level,result);
                            ReviewShortCableRecommendation(request.PhaseIndex,level);
                        }
                    };
                    acquisition.HostWaveformCaptured+=wave;acquisition.HostNoiseCaptured+=noise;
                    acquisition.HostSignalMetricsCaptured+=metrics;acquisition.HostCaptureCompleted+=completed;
                    await acquisition.HostAcquireAsync(request.CableLengthMeters,
                        request.VelocityMPerUs, chargePc, request.ShortCable);
                    if(!CanContinueHardwareAcquisition() || !acquisition.LastCaptureSucceeded)return false;
                    string saved;
                    if(request.AllLevels&&(!savedLevelSignatures.TryGetValue(LiveKey(request.PhaseIndex,level),out saved)||saved!=LevelSignature(request.PhaseIndex,level)))
                        return false; // Pending review/save must never be replaced by the next charge level.
                    return true;
                }
                finally {
                    checked{hardwareSessionGeneration++;}
                    acquisition.HostWaveformCaptured-=wave;acquisition.HostNoiseCaptured-=noise;
                    acquisition.HostSignalMetricsCaptured-=metrics;acquisition.HostCaptureCompleted-=completed;
                    acquisition.HostAutomaticThresholdResolved=null;
                    RetainOrDisposeAcquisition(acquisition);
                }
        }

        private string[] ResolveHardwareLevelCodes(string levelCode, bool allLevels)
        {
            // ALL_LEVELS describes a batch, not a charge value. Validate each real
            // level before acquiring anything; the operator still sets the source.
            string[] levels = allLevels
                ? System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(LevelCodes, c => c != "TDR"))
                : new[] { NormalizeLevelCode(levelCode) };
            foreach (string level in levels) {
                double charge;
                if (Array.IndexOf(LevelCodes, level) < 0 || !TryChargePc(level, out charge) || (IsCapacitiveLoad && level == "TDR"))
                    throw new ArgumentException("请选择有效的校准档位。");
            }
            return levels;
        }

        private static bool TryChargePc(string levelCode, out double chargePc)
        {
            chargePc = 0;
            if (String.Equals(levelCode, "TDR", StringComparison.OrdinalIgnoreCase)) { chargePc = 100; return true; }
            if (String.IsNullOrWhiteSpace(levelCode)) return false;
            string code = levelCode.Trim();
            double value;
            if (code.EndsWith("nC", StringComparison.OrdinalIgnoreCase) &&
                Double.TryParse(code.Substring(0, code.Length - 2), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value)) chargePc = value * 1000;
            else if (code.EndsWith("pC", StringComparison.OrdinalIgnoreCase) &&
                Double.TryParse(code.Substring(0, code.Length - 2), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value)) chargePc = value;
            return chargePc >= 1 && chargePc <= 500000;
        }
    }
}
