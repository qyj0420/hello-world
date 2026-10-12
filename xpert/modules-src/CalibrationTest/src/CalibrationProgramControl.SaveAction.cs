using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        // UI snapshots contain no samples and never read disk from Paint.
        readonly Dictionary<string,string> savedLevelSignatures = new Dictionary<string,string>();
        // Leaving the page is not a calibration-readiness decision. Optional
        // ranging records must never hold the user on the calibration page.
        [System.ComponentModel.Browsable(false)]
        public bool HasUnsavedChargeCalibrationChanges
        {
            get {
                if(SimulationMode)return HasUnsavedCalibrationChanges;
                lock(stateLock) {
                    for(int phase=-1;phase<3;phase++)
                        foreach(string level in GetPhaseStateNoLock(phase).Waveforms.Keys) {
                            if(level=="TDR")continue;
                            string saved;
                            // The existing saved charge record remains available
                            // when optional ranging/display cursors are edited.
                            if(!savedLevelSignatures.TryGetValue(LiveKey(phase,level),out saved))return true;
                        }
                }
                return false;
            }
        }
        [System.ComponentModel.Browsable(false)]
        public string UnsavedCalibrationSummary
        {
            get {
                // Explain the same per-level signature comparison used by
                // HasLiveChanges. A saved visible level must not hide a dirty
                // TDR or another phase; generating this text never marks saved.
                var pending=new List<string>(); bool pendingRanging=false;
                lock(stateLock) {
                    for(int phaseIndex=-1;phaseIndex<3;phaseIndex++) {
                        var phase=GetPhaseStateNoLock(phaseIndex);
                        foreach(string level in LevelCodes) {
                            if(!SimulationMode&&level=="TDR")continue;
                            if(!phase.Waveforms.ContainsKey(level))continue;
                            string saved;
                            bool previouslySaved=savedLevelSignatures.TryGetValue(LiveKey(phaseIndex,level),out saved);
                            if(previouslySaved&&(!SimulationMode||saved==LevelSignature(phaseIndex,level)))continue;
                            string purpose=level=="TDR"
                                ? CalibrationText("测距结果／电缆长度","ranging result / cable length")
                                : CalibrationText("校准系数","calibration coefficient");
                            string state=previouslySaved?CalibrationText("已修改，需重新保存","changed; save again")
                                :CalibrationText("未保存","not saved");
                            pending.Add(PhaseCodeFromIndex(phaseIndex)+" · "+level+CalibrationText("：",": ")+purpose+" "+state);
                            pendingRanging|=level=="TDR";
                        }
                    }
                }
                if(pending.Count==0) {
                    if(!HasUnsavedChargeCalibrationChanges)return String.Empty;
                    return CalibrationText("校准档案或参数尚未保存，或已保存文件未通过核验，请检查后保存。",
                        "The calibration archive or parameters are not saved, or the saved file failed verification. Check and save them.");
                }
                string summary=String.Join("\r\n",pending);
                if(pendingRanging&&!SimulationMode)summary+="\r\n\r\n"+CalibrationText(
                    "TDR 测距与电量校准分别保存。请先切换到上述 TDR 相别，核对后点击“保存电缆长度”；保存 5 nC 等档位的系数不会保存 TDR 测距结果。",
                    "Ranging and charge calibration are saved separately. Select the listed TDR phase, review it and click Save cable length first. Saving a charge level such as 5 nC does not save TDR ranging.");
                return summary;
            }
        }
        public string GetLevelSaveState(int phaseIndex,string levelCode)
        {
            ValidatePhaseIndex(phaseIndex);string level=NormalizeLevelCode(levelCode);
            lock(stateLock) {
                string signature=LevelSignature(phaseIndex,level),saved;
                if(signature.Length==0)return "Empty";
                Xiongxin.Calibration.CalibrationCaptureResult capture;
                if(liveCaptures.TryGetValue(LiveKey(phaseIndex,level),out capture)&&capture.QualityOverrideForReview)return "Review";
                if(savedLevelSignatures.TryGetValue(phaseIndex+":"+level,out saved))return signature==saved?"Saved":"Changed";
                return "Unsaved";
            }
        }
        private string LevelSaveStateCaption(string state)
        {
            return state=="Saved"?CalibrationText("已保存","Saved"):state=="Changed"?CalibrationText("待重存","Changed"):
                state=="Review"?CalibrationText("待复核","Review"):state=="Unsaved"?CalibrationText("待保存","Unsaved"):CalibrationText("未采集","No data");
        }
        string LevelSignature(int phaseIndex, string level)
        {
            PhaseState phase=GetPhaseStateNoLock(phaseIndex); WaveformData wave; MarkerData marker;TdrAutoAnalysisResult analysis;
            if(!phase.Waveforms.TryGetValue(level,out wave)) return String.Empty;
            phase.Markers.TryGetValue(level,out marker);
            phase.Analyses.TryGetValue(level,out analysis);
            LiveLevelParameters parameters=null;
            bool scoped=!SimulationMode&&liveLevelParameters.TryGetValue(LiveKey(phaseIndex,level),out parameters);
            if(!scoped)parameters=null;
            return String.Join("|",new[]{RuntimeHelpers.GetHashCode(wave).ToString(CultureInfo.InvariantCulture),
                (parameters==null?phase.CableLengthMeters:parameters.Length).ToString("R",CultureInfo.InvariantCulture),
                (parameters==null?phase.VelocityMPerUs:parameters.Velocity).ToString("R",CultureInfo.InvariantCulture),
                (parameters==null?phase.ShortCable:parameters.ShortCable).ToString(),(parameters==null?phase.WideBand:parameters.WideBand).ToString(),
                (parameters==null?phase.BandwidthCutoffMHz:parameters.Cutoff).ToString("R",CultureInfo.InvariantCulture),UseFirstArrivalTiming.ToString(),
                marker==null?"":marker.StartTimeSeconds.ToString("R",CultureInfo.InvariantCulture),
                marker==null?"":marker.DistanceMarkerSeconds.ToString("R",CultureInfo.InvariantCulture),
                analysis==null?"":analysis.IncidentIndex.ToString(CultureInfo.InvariantCulture),
                analysis==null?"":analysis.ReflectionIndex.ToString(CultureInfo.InvariantCulture),
                (ReflectionSelectionNoLock(phase,level,false)?.PeakAnchored==true).ToString(),
                IsCapacitiveLoad&&analysis!=null?analysis.IncidentTimeSeconds.ToString("R",CultureInfo.InvariantCulture):""});
        }
        void RememberSavedLevels()
        {
            lock(stateLock) {
                savedLevelSignatures.Clear();
                for(int phase=-1;phase<3;phase++) foreach(string level in LevelCodes)
                { string value=LevelSignature(phase,level); if(value.Length>0) savedLevelSignatures[phase+":"+level]=value; }
            }
        }
        public bool SelectedCalibrationSaved
        {
            get { lock(stateLock) {
                // Real captures are persisted per selected phase/level, including
                // when the all-level selector remains expanded after a batch.
                foreach(string level in SimulationMode&&CalibrateAllLevels?FilterMeasurementLevels(LevelCodes):new[]{selectedLevelCode}) {
                    string signature=LevelSignature(selectedPhaseIndex,level), saved;
                    if(signature.Length==0||!savedLevelSignatures.TryGetValue(selectedPhaseIndex+":"+level,out saved)||signature!=saved)return false;
                }
                return true;
            } }
        }
        public bool SelectedCalibrationHasData
        { get { lock(stateLock) return GetPhaseStateNoLock(selectedPhaseIndex).Waveforms.ContainsKey(selectedLevelCode); } }
        public bool RequestSaveOrDelete()
        {
            if(IsBusy||!SelectedCalibrationHasData)return false;
            if (IsDistanceMode) return SaveMeasuredCableLength();
            if(!SimulationMode)return SelectedCalibrationSaved?DeleteSelectedLiveCalibration():SaveLiveCalibration(SelectedPhaseIndex,SelectedLevelCode);
            if(SelectedCalibrationSaved) {
                if(HasUnsavedCalibrationChanges) {
                    // Never delete when another phase or metadata has unsaved edits.
                    // Revert the action to Save rather than trapping the user in an
                    // unclickable Delete state. The next render follows the state.
                    savedLevelSignatures.Clear(); Invalidate();
                    return CalibrationFailure("检测到未保存更改，按钮已切换为保存，请确认保存后再操作。","Unsaved changes detected. The action is now Save; confirm saving first.");
                }
                if(!IsCalibrationSavedValid)return CalibrationFailure("校准文件已变化，请重新加载后操作。","Calibration file changed; reload before continuing.");
                return RequestDelete();
            }
            return RequestSave();
        }
        string CalibrationStartCaption
        { get { return hostAcquisitionActive?CalibrationText("停止采集", "Stop acquisition"):IsDistanceMode ? CalibrationText("开始测距", "Start ranging") : CalibrationText("开始校准", "Start calibration")+"\n"+(CalibrateAllLevels?CalibrationText("全部档位","All levels"):LevelLabels[Array.IndexOf(LevelCodes,SelectedLevelCode)]); } }
        public bool AutoSaveQualifiedCalibration()
        {
            if(!SimulationMode||!HasCalibrationData||RequiresPhaseVelocityReview(SelectedPhaseIndex))return false;
            lock(stateLock) {
                PhaseState phase=GetPhaseStateNoLock(SelectedPhaseIndex);
                foreach(string level in CalibrateAllLevels?LevelCodes:new[]{SelectedLevelCode}) {
                    TdrAutoAnalysisResult result; MarkerData marker;
                    if(!phase.Analyses.TryGetValue(level,out result)||result.SearchStatus!=TdrSearchStatus.Ok||
                        result.IncidentMagnitude<=0||result.ReflectionMagnitude<=0||
                        !phase.Markers.TryGetValue(level,out marker)||marker.DistanceMarkerSeconds<=marker.StartTimeSeconds)return false;
                }
            }
            return SaveCalibrationFile();
        }
    }
}
