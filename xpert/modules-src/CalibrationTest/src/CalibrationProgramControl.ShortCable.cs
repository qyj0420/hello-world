using System;
using System.Globalization;
using System.Threading.Tasks;
using Xiongxin.Calibration;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        private bool shortCableWriteInProgress;
        private string shortCableRecommendationNotice;
        // Exact program-owned catalog text also keeps the explicit zh-TW
        // wording; unfilled locales retain the established English fallback.
        private string ShortCableText(string chinese,string english)
        {
            string caption;
            return Xiongxin.OWTS.Localization.UiLanguage.TryTranslate(LanguageIndex,english,out caption)
                ?caption:CalibrationText(chinese,english);
        }
        public bool ShortCableRecommended {get;private set;}
        private void ReviewShortCableRecommendationOnAnalysis(object sender,EventArgs args)
        {ReviewShortCableRecommendation(SelectedPhaseIndex,SelectedLevelCode);}
        // This pure recommendation never creates a task or writes any output.
        private void ReviewShortCableRecommendation(int phaseIndex,string level)
        {
            bool recommend=false;double roundTrip=Double.NaN,ratio=Double.NaN;
            lock(stateLock) {
                var phase=GetPhaseStateNoLock(phaseIndex);WaveformData current;TdrAutoAnalysisResult analysis;
                if(!IsCapacitiveLoad&&phase.Waveforms.TryGetValue(level,out current)&&
                    phase.Analyses.TryGetValue(level,out analysis)&&analysis!=null&&analysis.SearchStatus==TdrSearchStatus.Ok&&
                    analysis.IncidentIndex>=0&&analysis.ReflectionIndex>analysis.IncidentIndex&&analysis.ReflectionIndex<current.Samples.Length) {
                    double baseline,sigma;TdrReflectionAnalyzer.RawTimingStatistics(current.Samples,out baseline,out sigma);
                    recommend=AutomaticShortCablePolicy.ShouldEnable(phase.CableLengthMeters,phase.VelocityMPerUs,
                        Math.Abs(current.Samples[analysis.IncidentIndex]-baseline),Math.Abs(current.Samples[analysis.ReflectionIndex]-baseline),
                        false,out roundTrip,out ratio);
                }
            }
            ShortCableRecommended=recommend;
            if(!recommend&&shortCableRecommendationNotice!=null&&lastCalibrationError==shortCableRecommendationNotice)
                lastCalibrationError="";
            if(recommend&&!GetPhaseShortCable(phaseIndex)) {
                lastCalibrationError=shortCableRecommendationNotice=ShortCableText("建议开启短电缆阻抗；请待采集停止后手动设置。", "Short-cable impedance is recommended. Set it manually after acquisition has stopped.");
                WriteCalibrationDiagnostic("Short-cable recommendation only; no output written. roundTripUs="+
                    roundTrip.ToString("R",CultureInfo.InvariantCulture)+";reflectionRatio="+ratio.ToString("R",CultureInfo.InvariantCulture));
                Invalidate();
            }
        }
        private async void RequestManualShortCableSetting(bool value)
        {
            try {await SetManualShortCableWhenIdleAsync(SelectedPhaseIndex,SelectedLevelCode,value);}
            catch(Exception error) {WriteCalibrationDiagnostic("Manual short-cable impedance: "+error);}
        }
        internal async Task<bool> SetManualShortCableWhenIdleAsync(int phaseIndex,string level,bool value)
        {
            if(SimulationMode||IsDisposed||Disposing||HardwareOperationBusy||activeAcquisition==null||
                !activeAcquisition.HostSessionReusable||!CanContinueHardwareAcquisition()) {
                lastCalibrationError=ShortCableText("请待采集停止并确认设备状态后再设置阻抗。", "Wait for acquisition to stop and the device state to be confirmed before setting impedance.");
                Invalidate();return false;
            }
            var acquisition=activeAcquisition;string project=ProjectPath;
            shortCableWriteInProgress=true;Invalidate();
            try {
                await acquisition.HostSetShortCableWhenIdleAsync(value);
                if(!CanContinueHardwareAcquisition()||project!=ProjectPath||!Object.ReferenceEquals(activeAcquisition,acquisition))return false;
                lock(stateLock) {
                    GetPhaseStateNoLock(phaseIndex).ShortCable=value;
                    RememberLiveLevelParameters(phaseIndex,level);
                    // Existing capture provenance stays unchanged: it was not
                    // acquired under this newly confirmed output setting.
                }
                lastCalibrationError=ShortCableText("阻抗设置已确认；已有波形保留原采集配置。", "Impedance setting is confirmed. Existing waveforms retain their original acquisition setting.");
                AuditQualityOperation(phaseIndex,level,"ManualShortCable","WriteConfirmed",value.ToString());
                return true;
            }
            catch(Exception error) {
                lastCalibrationError=ShortCableText("阻抗切换未完成，请检查设备状态后重试。", "Impedance switching is incomplete. Check the device state before retrying.");
                WriteCalibrationDiagnostic("Manual short-cable impedance failed; owned tasks retained: "+error);
                calibrationStopRequested=true;return false;
            }
            finally {shortCableWriteInProgress=false;if(!IsDisposed&&!Disposing)Invalidate();}
        }
    }
}
