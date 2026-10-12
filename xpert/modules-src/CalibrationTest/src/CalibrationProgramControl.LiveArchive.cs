using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using HVX.CalibrationStorage;
using HVX.SignalProcessing;
using Xiongxin.Calibration;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        private readonly Dictionary<string,CalibrationCaptureResult> liveCaptures=new Dictionary<string,CalibrationCaptureResult>();
        private readonly Dictionary<string,WaveformData> liveWaveSources=new Dictionary<string,WaveformData>();
        private readonly Dictionary<string,string> liveSavedFiles=new Dictionary<string,string>();
        private static string LiveKey(int phase,string level){return phase+":"+level;}
        private void MarkLiveProjectChanged()
        {
            if(SimulationMode)return;
            NotifyPersistedCableStructureChanged();
            Invalidate();
        }

        private void ReceiveLiveCapture(int phaseIndex,string level,CalibrationCaptureResult capture)
        {
            if(SimulationMode||capture==null||IsDisposed)return;
            if(capture.CapacitiveLoad!=IsCapacitiveLoad) { lastCalibrationError="采集负载类型与当前项目不一致，未接收校准。";return; }
            var phase=GetPhaseStateNoLock(phaseIndex);WaveformData wave;
            if(!phase.Waveforms.TryGetValue(level,out wave)||!wave.Samples.SequenceEqual(capture.Average))return;
            string key=LiveKey(phaseIndex,level);CalibrationCaptureResult previous;WaveformData previousWave;
            if(liveCaptures.TryGetValue(key,out previous)&&Object.ReferenceEquals(previous,capture)&&
                liveWaveSources.TryGetValue(key,out previousWave)&&Object.ReferenceEquals(previousWave,wave))return;
            reflectionSelections.Remove(key);
            liveCaptures[key]=capture;liveWaveSources[key]=wave;
            // A new unresolved SNR must clear the previous capture's finite
            // display value; the public finite-only metrics API stays unchanged.
            if(capture.Assessment!=null) lock(stateLock) {
                signalToNoiseDb=IsFinite(capture.Assessment.SnrDb)?capture.Assessment.SnrDb:Double.NaN;
                peakVoltageV=IsFinite(capture.Assessment.IncidentPeakV)?capture.Assessment.IncidentPeakV:Double.NaN;
                noiseVoltageV=IsFinite(capture.Assessment.NoiseRmsV)?capture.Assessment.NoiseRmsV:Double.NaN;
            }
            if(capture.Bandwidth!=null)SetPhaseBandwidth(phaseIndex,capture.Bandwidth.CutoffMHz>0,capture.Bandwidth.CutoffMHz>0?capture.Bandwidth.CutoffMHz:60);
            if(!IsCapacitiveLoad && capture.Reflection!=null && capture.Reflection.Valid) {
                var active=ActiveWaveformNoLock(phase,level,wave);
                var result=BuildManualResult(active,phase,capture.Reflection.IncidentIndex,capture.Reflection.ReflectionIndex,null);
                result.Message=capture.Reflection.Message;result.Confidence=Math.Abs(capture.Reflection.Correlation);
                StoreAnalysisNoLock(phase,level,active,result);
                if(level!="TDR") DetectAndStoreJoints(phaseIndex,level,capture);
            } else {
                var active=ActiveWaveformNoLock(phase,level,wave);
                StoreAnalysisNoLock(phase,level,active,IncidentOnlyAnalysis(active,capture));
            }
            if(!IsCapacitiveLoad && capture.Bandwidth!=null) {
                lastCalibrationError=ApplyAutomaticBandwidthOutcome(phase,capture);
                if(capture.Reflection!=null&&capture.Reflection.Valid&&!capture.Bandwidth.Sufficient&&capture.Bandwidth.Stable!=true&&!capture.Bandwidth.DisplayFallback)
                    MessageBox.Show(FindForm(),lastCalibrationError,CalibrationText("请核对反射波","Review reflection"),MessageBoxButtons.OK,MessageBoxIcon.Warning);
            }
            if(!IsCapacitiveLoad&&(capture.Reflection==null||!capture.Reflection.Valid))NotifyMissingReflectionOnce(phaseIndex,level);
            if(level!="TDR" && !capture.QualityOverrideForReview && capture.Assessment!=null && capture.Assessment.SaveRecommended &&
                (IsCapacitiveLoad || capture.Reflection!=null&&capture.Reflection.Valid&&capture.Bandwidth!=null&&capture.Bandwidth.Sufficient))
                SaveLiveCalibration(phaseIndex,level);
            ResetViewInternal();Invalidate();
        }

        private bool HasLiveChanges()
        {
            for(int phase=-1;phase<3;phase++)foreach(string level in GetPhaseStateNoLock(phase).Waveforms.Keys) {
                string value;if(!savedLevelSignatures.TryGetValue(LiveKey(phase,level),out value)||value!=LevelSignature(phase,level))return true;
            }
            return false;
        }
        private bool IsLiveSavedValid()
        {
            if(HasLiveChanges())return false;
            try {foreach(var entry in liveSavedFiles) {
                if(!File.Exists(entry.Key)||LiveCalibrationRecord.HashFile(entry.Key)!=entry.Value)return false;
                LiveCalibrationRecord.Load(ProjectPath,entry.Key,false);
            }return HasCalibrationData;}
            catch(Exception ex)when(ex is IOException||ex is InvalidDataException||ex is InvalidOperationException||ex is ArgumentException){return false;}
        }
        private bool SaveLiveCalibration(int phaseIndex,string level)
        {
            AuditQualityOperation(phaseIndex,level,"CalibrationSave","Requested","");
            try {
                string key=LiveKey(phaseIndex,level);CalibrationCaptureResult source;WaveformData original;
                RestoreLiveLevelParameters(phaseIndex,level);
                var phase=GetPhaseStateNoLock(phaseIndex);WaveformData wave;MarkerData marker;TdrAutoAnalysisResult analysis;
                if(!liveCaptures.TryGetValue(key,out source)||!liveWaveSources.TryGetValue(key,out original)||
                    !phase.Waveforms.TryGetValue(level,out wave)||!Object.ReferenceEquals(wave,original))
                    throw new InvalidOperationException("没有本次实机采集来源，不将外部或仿真波形保存为实机系数。");
                if(level=="TDR")throw new InvalidOperationException("测距模式请保存电缆长度，不生成放电量系数。");
                LiveCalibrationRecord savedSource=null;
                if(!String.IsNullOrEmpty(source.SavedRecordPath)) {
                    string expectedHash;
                    if(!liveSavedFiles.TryGetValue(source.SavedRecordPath,out expectedHash)||LiveCalibrationRecord.HashFile(source.SavedRecordPath)!=expectedHash)
                        throw new IOException("已加载校准档案发生变化，请重新加载。");
                    savedSource=LiveCalibrationRecord.Load(ProjectPath,source.SavedRecordPath,true);
                    if(savedSource.Version==1) {
                        // V1's saved coefficient has unknown historical processing.
                        // A no-op save must not fabricate new HP-only provenance.
                        string signature;
                        if(!savedLevelSignatures.TryGetValue(key,out signature)||signature!=LevelSignature(phaseIndex,level))
                            throw new InvalidDataException("历史引用式校准已修改，不能将未知处理口径重算为新校准；请重新采集，或放弃修改后加载原件。");
                        lastCalibrationError="";AuditQualityOperation(phaseIndex,level,"CalibrationSave","AlreadySaved","V1 original coefficient and provenance retained.");
                        return true;
                    }
                }
                else if(!String.IsNullOrEmpty(source.WithdrawnRecordPath)) {
                    if(String.IsNullOrEmpty(source.WithdrawnRecordSha256)||LiveCalibrationRecord.HashFile(source.WithdrawnRecordPath)!=source.WithdrawnRecordSha256)
                        throw new IOException("已撤销校准的备份来源发生变化，不能重新保存。");
                    savedSource=LiveCalibrationRecord.Load(ProjectPath,source.WithdrawnRecordPath,true);
                    if(savedSource.Version==1) {
                        // Restore the verified historical record unchanged, not a
                        // remeasured coefficient. Raw references still must pass.
                        if(String.IsNullOrEmpty(source.WithdrawnLevelSignature)||source.WithdrawnLevelSignature!=LevelSignature(phaseIndex,level)||
                            !wave.Samples.SequenceEqual(savedSource.Average))
                            throw new InvalidDataException("撤销后的历史校准已修改，不能恢复为原校准；请重新采集或重新加载备份。");
                        string restored=savedSource.Save(ProjectPath);
                        liveSavedFiles[restored]=LiveCalibrationRecord.HashFile(restored);
                        source.SavedRecordPath=restored;source.WithdrawnRecordPath=null;source.WithdrawnRecordSha256=null;source.WithdrawnLevelSignature=null;
                        savedLevelSignatures[key]=LevelSignature(phaseIndex,level);lastCalibrationError="";
                        AuditQualityOperation(phaseIndex,level,"CalibrationSave","Restored","Verified V1 original coefficient; preprocessing remains historical/unknown.");
                        System.Threading.Interlocked.Increment(ref calibrationSavedSequence);
                        var restoredHandler=CalibrationSaved;if(restoredHandler!=null)try{restoredHandler(this,EventArgs.Empty);}catch(Exception error){System.Diagnostics.Debug.WriteLine("CalibrationSaved listener: "+error.Message);}
                        Invalidate();return true;
                    }
                }
                if(String.IsNullOrWhiteSpace(source.DeviceSerial)||(savedSource==null&&(source.RawPaths==null||source.RawPaths.Length==0)))
                    throw new InvalidOperationException("设备标识或本次原始采集记录缺失，不能保存校准系数。");
                var warnings=new List<string>();
                if(source.QualityOverrideForReview)warnings.Add("本次计算采用了操作人员选择继续的低质量脉冲。");
                bool capacitor=IsCapacitiveLoad;
                phase.Markers.TryGetValue(level,out marker);
                if(source.CapacitiveLoad!=capacitor || !phase.Analyses.TryGetValue(level,out analysis)||
                    !IsFinite(analysis.IncidentMagnitude)||analysis.IncidentMagnitude<=0||analysis.IncidentIndex<0||analysis.IncidentIndex>=wave.Samples.Length)
                    throw new InvalidOperationException("缺少有效入射幅值，无法计算校准系数；原始采集数据已保留。");
                bool paired=!capacitor&&marker!=null&&analysis.SearchStatus==TdrSearchStatus.Ok&&
                    IsFinite(analysis.ReflectionMagnitude)&&analysis.ReflectionMagnitude>0&&analysis.ReflectionIndex>analysis.IncidentIndex&&
                    analysis.ReflectionIndex<wave.Samples.Length&&IsFinite(marker.DistanceMarkerSeconds)&&marker.DistanceMarkerSeconds>marker.StartTimeSeconds;
                var selection=ReflectionSelectionNoLock(phase,level,false);
                bool peakSelection=selection!=null&&selection.Confirmed&&selection.PeakAnchored;
                if(peakSelection&&UseFirstArrivalTiming&&!paired)
                    throw new InvalidOperationException(CalibrationText("首达未确认，请重新选择入射和反射波峰后保存。", "First arrival is unconfirmed. Reselect the incident and reflection peaks before saving."));
                if(source.Assessment!=null&&source.Assessment.CircuitSuspected)
                    warnings.Add("采集时出现回路参考差异提示："+source.Assessment.Message);
                double chargePc;if(!TryChargePc(level,out chargePc))throw new InvalidOperationException("校准量无效。");
                var quality=source.Assessment;
                if(source.Windows!=null&&source.Windows.Length>0) {
                    var windows=source.Windows;
                    int chargeIndex=peakSelection?analysis.IncidentIndex:source.Assessment!=null?source.Assessment.IncidentPeakIndex:analysis.IncidentIndex;
                    int start=Math.Max(8,chargeIndex-(peakSelection?2:64)),end=Math.Min(wave.Samples.Length,chargeIndex+(peakSelection?3:65));
                    quality=HVX.OWTS.Task38.CalibrationIncidentQuality.Analyze(windows,Math.Min(200,start),start,end,chargePc,
                        Xiongxin.Scope.Settings.Ranges[source.RangeIndex],false,source.RangeIndex+"/"+source.ShortCable+"/"+PdLinearHighPass.FilterId,null);
                    if(peakSelection&&quality.IncidentPeakIndex!=chargeIndex)
                        throw new InvalidOperationException(CalibrationText("所选入射波峰未确认，请重新选峰。", "The selected incident peak is unconfirmed. Reselect the peak."));
                }else {
                    if(source.CoefficientProcessing!="HP10K_NO_LP")
                        throw new InvalidOperationException("历史系数未记录无低通计算口径，请重新采集后保存；原始校准保持不变。");
                    if(peakSelection&&(source.Assessment==null||source.Assessment.IncidentPeakIndex!=analysis.IncidentIndex))
                        throw new InvalidOperationException(CalibrationText("修改入射波峰需要重新采集；原校准已保留。", "Changing the incident peak requires a new acquisition. The original calibration is retained."));
                }
                if(quality==null)throw new InvalidOperationException("缺少本次采集的质量记录。");
                if(!IsFinite(quality.CoefficientPcPerV)||quality.CoefficientPcPerV<=0)
                    throw new InvalidOperationException("高通后未低通的入射波不能计算有效系数，原始数据保留。");
                if(quality.PulseCount<3)warnings.Add("本次仅有 "+quality.PulseCount+" 个有效脉冲，已全部用于计算；重复性证据不足，请核对结果。");
                if(!quality.SaveRecommended)warnings.Add("当前游标和滤波下的质量提示："+quality.Message);
                var processed=ActiveWaveformNoLock(phase,level,wave);
                if(paired && ReflectionBandwidth.Ratio(processed.Samples,analysis.IncidentIndex,analysis.ReflectionIndex,Math.Max(4,Math.Min(64,(analysis.ReflectionIndex-analysis.IncidentIndex)/4)))<.1)
                    warnings.Add("反射幅值不足入射的 10%，请核对带宽与反射位置。");
                double correlation=capacitor?0:paired?PacketCorrelation(processed.Samples,analysis.IncidentIndex,analysis.ReflectionIndex,Math.Max(4,Math.Min(48,(analysis.ReflectionIndex-analysis.IncidentIndex)/12))):Double.NaN;
                if(paired&&Math.Abs(correlation)<.6)warnings.Add("入射与反射波形相关性偏低。");
                string warning=String.Join("\r\n",warnings.Distinct());
                if(warning.Length>0){
                    bool accepted=ConfirmQualityWarningSaveForTests!=null?ConfirmQualityWarningSaveForTests(warning):
                        MessageBox.Show(FindForm(),CalibrationQualitySaveCustomerCaption,CalibrationText("校准质量提示","Calibration quality warning"),MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)==DialogResult.Yes;
                    AuditQualityOperation(phaseIndex,level,"CalibrationQualityWarning",accepted?"Accepted":"Cancelled",warning);
                    if(!accepted)return false;
                }
                double measuredVelocity=capacitor?0:paired?2*phase.CableLengthMeters/((marker.DistanceMarkerSeconds-marker.StartTimeSeconds)*1e6):phase.VelocityMPerUs;
                double incidentTime=paired?marker.StartTimeSeconds:analysis.IncidentTimeSeconds;
                if(!IsFinite(incidentTime)||incidentTime<0)throw new InvalidOperationException("入射计时尚未通过核对。");
                var record=new LiveCalibrationRecord {Phase=PhaseCodeFromIndex(phaseIndex),Level=level,MeasurementKind=capacitor?"Capacitor":"Cable",
                    CircuitReferenceStatus=quality.CircuitReferenceChecked?"Checked":"NotChecked",
                    ReflectionStatus=capacitor?"NotApplicable":paired?"Detected":"NotDetected",VelocitySource=capacitor?"NotApplicable":paired?"Measured":"Configured",
                    ReflectionNotice=!capacitor&&!paired?LiveCalibrationRecord.UnconfirmedReflectionNotice:"",
                    ReflectionSelectionMethod=selection?.Confirmed==true?"Manual":paired?"Automatic":"Unspecified",
                    PeakAnchoredSelection=peakSelection&&paired,
                    ManualIncidentSeconds=selection?.Confirmed==true?(peakSelection?wave.X0Seconds+analysis.IncidentIndex*wave.DxSeconds:marker.StartTimeSeconds):Double.NaN,
                    ManualReflectionSeconds=selection?.Confirmed==true?(peakSelection?wave.X0Seconds+analysis.ReflectionIndex*wave.DxSeconds:marker.DistanceMarkerSeconds):Double.NaN,
                    QualityWarningAcknowledged=warning.Length>0,QualityWarning=warning,
                    PicoCoupling=source.PicoCoupling,DcRemoval=source.DcRemoval,
                    DeviceSerial=source.DeviceSerial,DeviceModel=source.DeviceModel,RangeIndex=source.RangeIndex,
                    FilterId=PdLinearHighPass.FilterId,ProjectHash=LiveCalibrationRecord.Identity(ProjectPath),CreatedUtc=DateTime.UtcNow.ToString("o"),
                    AcquisitionMode=source.AcquisitionMode,LengthM=capacitor?0:phase.CableLengthMeters,VelocityMPerUs=measuredVelocity,
                    SampleRateHz=source.SampleRateHz,IncidentSeconds=incidentTime,ReflectionSeconds=capacitor?0:paired?marker.DistanceMarkerSeconds:Double.NaN,
                    CoefficientPcPerV=quality.CoefficientPcPerV,CoefficientProcessing="HP10K_NO_LP",CoefficientIncidentPeakIndex=quality.IncidentPeakIndex,
                    AttenuationDb=capacitor?0:paired?20*Math.Log10(analysis.IncidentMagnitude/analysis.ReflectionMagnitude):Double.NaN,BandwidthMHz=phase.WideBand?phase.BandwidthCutoffMHz:0,LowPassFilterId=phase.WideBand?PdLinearLowPass.FilterId:"",
                    SnrDb=quality.SnrDb,Correlation=correlation,ShortCable=source.ShortCable,FirstArrival=UseFirstArrivalTiming,
                    IncidentPeakIndex=analysis.IncidentIndex,ReflectionPeakIndex=paired?analysis.ReflectionIndex:-1,
                    PulseCount=quality.PulseCount,ChargePc=chargePc,TriggerThresholdV=source.ThresholdV,Average=(double[])wave.Samples.Clone()};
                string prefix=Path.GetFullPath(ProjectPath).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
                record.RawFiles=savedSource!=null?savedSource.RawFiles:source.RawPaths.Select(p=>{
                    string full=Path.GetFullPath(p);if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new IOException("原始采集文件不属于当前项目。");
                    return new CalibrationRawReference{RelativePath=full.Substring(prefix.Length),Sha256=LiveCalibrationRecord.HashFile(full)};
                }).ToArray();
                record.Version=3;record.EffectiveWindows=null;
                string file=record.Save(ProjectPath);liveSavedFiles[file]=LiveCalibrationRecord.HashFile(file);
                // The freshly saved self-contained record becomes the source
                // immediately, not only after closing/reopening the page. This
                // keeps a subsequent cursor edit/save independent of raw cache.
                if(record.Version>=2){source.SavedRecordPath=file;source.WithdrawnRecordPath=null;source.WithdrawnRecordSha256=null;source.WithdrawnLevelSignature=null;}
                if(savedSource==null)LiveCalibrationRecord.RetireAveragedCaptureCache(ProjectPath,file);
                source.Assessment=quality;source.CoefficientProcessing="HP10K_NO_LP";
                // Preserve cursor delta while adopting the measured propagation speed.
                if(paired)phase.VelocityMPerUs=measuredVelocity;
                RememberLiveLevelParameters(phaseIndex,level);
                savedLevelSignatures[key]=LevelSignature(phaseIndex,level);lastCalibrationError="";
                AuditQualityOperation(phaseIndex,level,"CalibrationSave","Saved",
                    "CoefficientPcPerV="+record.CoefficientPcPerV.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+
                    "; CircuitReferenceStatus="+record.CircuitReferenceStatus+"; ReflectionStatus="+record.ReflectionStatus+"; VelocitySource="+record.VelocitySource+"; PicoCoupling="+record.PicoCoupling+"; DcRemoval="+record.DcRemoval+"; "+quality.Message);
                if(!String.IsNullOrEmpty(qualityAuditWarning))lastCalibrationError=qualityAuditWarning;
                System.Threading.Interlocked.Increment(ref calibrationSavedSequence);
                var handler=CalibrationSaved;if(handler!=null)try{handler(this,EventArgs.Empty);}catch(Exception ex){System.Diagnostics.Debug.WriteLine("CalibrationSaved listener: "+ex.Message);}
                Invalidate();return true;
            }catch(Exception ex){
                AuditQualityOperation(phaseIndex,level,"CalibrationSave","NotSaved",ex.Message);
                lastCalibrationError=RecordCalibrationCustomerFailure("Live calibration save",ex);return false;
            }
        }
        internal Func<string,bool> ConfirmQualityWarningSaveForTests;
        private bool SaveAllLiveCalibrations()
        {
            if(!HasCalibrationData)return CalibrationFailure("尚无实机校准波形可保存。","No live calibration waveform is available to save.");
            // Save charge levels only. TDR is an optional length review and is
            // saved explicitly by SaveMeasuredCableLength, never by navigation.
            bool any=false;
            try {
                for(int phase=-1;phase<3;phase++)foreach(string level in GetPhaseStateNoLock(phase).Waveforms.Keys.ToArray()) {
                    if(level=="TDR")continue;
                    string saved;if(savedLevelSignatures.TryGetValue(LiveKey(phase,level),out saved)&&saved==LevelSignature(phase,level)){any=true;continue;}
                    if(!SaveLiveCalibration(phase,level))return false;any=true;
                }
                return any&&!HasUnsavedChargeCalibrationChanges;
            }finally {for(int phase=-1;phase<3;phase++)RestoreLiveLevelParameters(phase,selectedLevelCode);}
        }
        private bool LoadLiveCalibrations()
        {
            try {
                if(HasLiveChanges())throw new InvalidOperationException("请先保存或放弃当前更改。");
                if(String.IsNullOrWhiteSpace(ProjectPath)||!Directory.Exists(ProjectPath))return false;
                int count=0;var failures=new List<string>();var loadedLevels=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // Prefer the current root-level record. A differently named old
                // archive must not overwrite an already validated phase/level.
                foreach(string file in LiveCalibrationRecord.EnumerateRecordFiles(ProjectPath)
                    .OrderBy(p=>String.Equals(Path.GetDirectoryName(p),ProjectPath,StringComparison.OrdinalIgnoreCase)?0:1)) try {
                    var r=LiveCalibrationRecord.Load(ProjectPath,file);
                    if(r.FirstArrival!=UseFirstArrivalTiming) {
                        failures.Add(Path.GetFileName(file)+": "+CalibrationText("记录计时方式与当前设置不同，请选用匹配计时方式；未改写原记录。","The recorded timing differs from the current setting. Select matching timing; the original record was not changed."));continue;
                    }
                    int phaseIndex=r.Phase=="ALL"?-1:r.Phase[0]-'A';
                    bool capacitor=r.MeasurementKind=="Capacitor";
                    if(capacitor!=IsCapacitiveLoad) {failures.Add(Path.GetFileName(file)+": "+CalibrationText("记录负载类型与当前项目不同。","The recorded load type differs from this project."));continue;}
                    string key=LiveKey(phaseIndex,r.Level);
                    if(loadedLevels.Contains(key)) {failures.Add(Path.GetFileName(file)+": an applicable current record already owns phase/level "+key+"; duplicate was not loaded.");continue;}
                    var phase=GetPhaseStateNoLock(phaseIndex);
                    if(!capacitor) {
                        phase.CableLengthMeters=r.IsTdr?r.LengthM:PersistedCalibrationLength(phaseIndex,r.LengthM);
                        phase.VelocityMPerUs=r.VelocityMPerUs;
                        if(!r.IsTdr&&r.HasMeasuredReflection)
                            phase.VelocityMPerUs=2*phase.CableLengthMeters/((r.ReflectionSeconds-r.IncidentSeconds)*1e6);
                    }
                    phase.ShortCable=r.ShortCable;phase.WideBand=r.BandwidthMHz>0;
                    phase.BandwidthCutoffMHz=r.BandwidthMHz>0?r.BandwidthMHz:60;
                    RememberLiveLevelParameters(phaseIndex,r.Level);
                    SetPhaseWaveform(phaseIndex,r.Level,r.Average,0,1/r.SampleRateHz);
                    var wave=phase.Waveforms[r.Level];
                    int incident=r.IncidentPeakIndex,reflection=r.ReflectionPeakIndex;
                    var active=ActiveWaveformNoLock(phase,r.Level,wave);
                    if(r.HasMeasuredReflection) {
                        var analysis=BuildManualResult(active,phase,incident,reflection,null);StoreAnalysisNoLock(phase,r.Level,active,analysis);
                        phase.Markers[r.Level]=new MarkerData(r.IncidentSeconds,r.ReflectionSeconds,r.ReflectionSeconds,r.LengthM);
                    }
                    liveWaveSources[key]=wave;
                    liveCaptures[key]=new CalibrationCaptureResult{Average=r.Average,SampleRateHz=r.SampleRateHz,
                        Windows=r.EffectiveWindows,SavedRecordPath=file,
                        CoefficientProcessing=r.CoefficientProcessing,
                        DeviceSerial=r.DeviceSerial,DeviceModel=r.DeviceModel,RangeIndex=r.RangeIndex,ShortCable=r.ShortCable,
                        PicoCoupling=r.PicoCoupling,DcRemoval=r.DcRemoval,
                        AcquisitionMode=r.AcquisitionMode,ThresholdV=r.TriggerThresholdV,CapacitiveLoad=capacitor,
                        Bandwidth=new ReflectionBandwidthResult{CutoffMHz=r.BandwidthMHz,Sufficient=capacitor||r.HasMeasuredReflection},
                        RawPaths=r.RawFiles.Select(f=>LiveCalibrationRecord.ResolveRaw(ProjectPath,f.RelativePath)).ToArray(),
                        Reflection=new TdrCorrelationResult{Valid=r.HasMeasuredReflection,Correlation=r.Correlation,IncidentIndex=incident,ReflectionIndex=reflection,Message=r.HasMeasuredReflection?"已保存的反射结果。":"反射尚未确认；已保存入射校准系数。"},
                        Assessment=new HVX.OWTS.Task38.CalibrationIncidentAssessment{SaveRecommended=!r.HasIncidentQualityWarning,SnrDb=r.SnrDb,PulseCount=r.PulseCount,IncidentPeakIndex=r.CoefficientIncidentPeakIndex>=0?r.CoefficientIncidentPeakIndex:incident,CoefficientPcPerV=r.CoefficientPcPerV,IncidentPeakV=r.ChargePc/r.CoefficientPcPerV,BaselineV=r.Average.Take(Math.Min(200,Math.Max(1,incident/2))).Average(),Quality=r.HasIncidentQualityWarning?"已保存（保留质量提示）":"已保存",
                            CircuitReferenceChecked=r.CircuitReferenceStatus=="Checked",
                            Message="已通过正式记录校验。"+(r.QualityWarningAcknowledged?r.QualityWarning:"")+(r.CircuitReferenceStatus=="NotChecked"?"回路参考比对未启用。":r.CircuitReferenceStatus=="Checked"?"":"历史记录未注明回路参考比对状态。")}};
                    if(!r.HasMeasuredReflection) {
                        var result=IncidentOnlyAnalysis(active,liveCaptures[key]);result.IncidentTimeSeconds=r.IncidentSeconds;
                        StoreAnalysisNoLock(phase,r.Level,active,result);
                        var selection=ReflectionSelectionNoLock(phase,r.Level,true);if(selection!=null)selection.NoticeShown=true;
                    }
                    if(r.PeakAnchoredSelection) {
                        if(!SetPhasePacketPeaks(phaseIndex,r.Level,incident,reflection))
                            throw new InvalidDataException("已保存的选峰证据不可用。");
                    } else if(r.ReflectionSelectionMethod=="Manual"&&!r.HasMeasuredReflection) {
                        // A saved editing coordinate without measured reflection
                        // remains a pending handle, not an invented physical peak.
                        var pending=ReflectionSelectionNoLock(phase,r.Level,true);
                        if(pending!=null&&IsFinite(r.ManualReflectionSeconds)) {
                            pending.Index=(int)Math.Round(r.ManualReflectionSeconds*r.SampleRateHz);pending.NoticeShown=true;
                        }
                    } else if(r.ReflectionSelectionMethod=="Manual") {
                        if(IsFinite(r.ManualIncidentSeconds)&&IsFinite(r.ManualReflectionSeconds)) {
                            // Historical manual times are timing coordinates,
                            // never a substitute for the stored physical peak.
                            var manual=BuildManualResult(active,phase,incident,reflection,phase.Analyses[r.Level]);
                            phase.Analyses[r.Level]=manual;
                            phase.Markers[r.Level]=new MarkerData(r.ManualIncidentSeconds,r.ManualReflectionSeconds,
                                r.ManualIncidentSeconds+2*phase.CableLengthMeters/(phase.VelocityMPerUs*1e6),
                                (r.ManualReflectionSeconds-r.ManualIncidentSeconds)*phase.VelocityMPerUs*1e6/2);
                            RememberManualReflectionNoLock(phase,r.Level);StoreAnalysisNoLock(phase,r.Level,active,manual);
                        }else RememberManualReflectionNoLock(phase,r.Level);
                    }
                    liveSavedFiles[file]=LiveCalibrationRecord.HashFile(file);savedLevelSignatures[key]=LevelSignature(phaseIndex,r.Level);loadedLevels.Add(key);count++;
                }catch(Exception error)when(error is IOException||error is InvalidDataException||error is InvalidOperationException||error is ArgumentException) {
                    failures.Add(Path.GetFileName(file)+": "+error.Message);
                }
                for(int phase=-1;phase<3;phase++)RestoreLiveLevelParameters(phase,selectedLevelCode);
                if(failures.Count>0)WriteCalibrationDiagnostic("Calibration records not loaded (applicable records loaded="+count+"): "+String.Join("; ",failures));
                // Applicability failures for another old level are diagnostic,
                // not a failure of a newly acquired, valid saved record.
                lastCalibrationError=count>0||failures.Count==0?String.Empty:CalibrationText(
                    "未找到适用于当前设置的校准记录。请核对计时方式与负载类型，或重新校准。",
                    "No calibration record matches the current settings. Check the timing and load type, or recalibrate.");
                Invalidate();return count>0;
            }catch(Exception ex){WriteCalibrationDiagnostic("Calibration records were not loaded: "+ex);return CalibrationFailure("校准记录未加载，请检查项目或重新校准。","Calibration records were not loaded. Check the project or recalibrate.");}
        }
        internal Func<bool> ConfirmDeleteLiveCalibrationForTests;
        private bool DeleteSelectedLiveCalibration()
        {
            string path=LiveCalibrationRecord.RecordPath(ProjectPath,SelectedPhaseCode,SelectedLevelCode);
            CalibrationCaptureResult capture;
            if(liveCaptures.TryGetValue(LiveKey(SelectedPhaseIndex,SelectedLevelCode),out capture)&&!String.IsNullOrEmpty(capture.SavedRecordPath))
                path=capture.SavedRecordPath;
            try {
                if(!File.Exists(path))throw new IOException("正式校准文件不存在。");
                bool confirmed=ConfirmDeleteLiveCalibrationForTests!=null?ConfirmDeleteLiveCalibrationForTests():
                    MessageBox.Show(FindForm(),"撤销所选正式校准？原始采集文件保留，校准记录将移为备份，可恢复。","删除校准",MessageBoxButtons.OKCancel,MessageBoxIcon.Question)==DialogResult.OK;
                if(!confirmed)return false;
                string digest=LiveCalibrationRecord.HashFile(path),expected;
                if(liveSavedFiles.TryGetValue(path,out expected)&&digest!=expected)throw new IOException("正式校准文件发生变化，请重新加载后再撤销。");
                var withdrawn=LiveCalibrationRecord.Load(ProjectPath,path);
                string backup=path+".withdrawn-"+Guid.NewGuid().ToString("N")+".bak";
                File.Move(path,backup);liveSavedFiles.Remove(path);
                if(capture!=null && String.Equals(capture.SavedRecordPath,path,StringComparison.OrdinalIgnoreCase)) {
                    capture.SavedRecordPath=null;
                    // V2+ remains usable after its disposable raw cache was retired.
                    // V1 restore additionally requires every original raw reference.
                    capture.WithdrawnRecordPath=backup;
                    capture.WithdrawnRecordSha256=digest;
                    capture.WithdrawnLevelSignature=LevelSignature(SelectedPhaseIndex,SelectedLevelCode);
                }
                savedLevelSignatures.Remove(LiveKey(SelectedPhaseIndex,SelectedLevelCode));Invalidate();return true;
            }catch(Exception ex){lastCalibrationError=RecordCalibrationCustomerFailure("Live calibration removal",ex);return false;}
        }
    }
}
