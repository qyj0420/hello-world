using System;
using System.IO;
using System.Linq;
using HVX.CalibrationStorage;
using HVX.SignalProcessing;
using Xiongxin.Calibration;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        private LiveCalibrationRecord PrepareTdrArchive(CalibrationCaptureResult source,PhaseState phase,MarkerData marker)
        {
            if(source==null||String.IsNullOrWhiteSpace(source.DeviceSerial))
                throw new InvalidOperationException("测距有效脉冲或设备来源缺失，尚未保存电缆长度。");
            LiveCalibrationRecord previous=null;
            if(!String.IsNullOrEmpty(source.SavedRecordPath)) {
                string digest;
                if(!liveSavedFiles.TryGetValue(source.SavedRecordPath,out digest)||LiveCalibrationRecord.HashFile(source.SavedRecordPath)!=digest)
                    throw new IOException("已加载测距档案发生变化，请重新加载。");
                previous=LiveCalibrationRecord.Load(ProjectPath,source.SavedRecordPath,true);
                if(!previous.IsTdr)throw new InvalidDataException("测距档案来源不匹配。");
            }
            if(previous==null&&(source.Windows==null||source.Windows.Length==0))
                throw new InvalidOperationException("测距有效平均来源缺失，尚未保存电缆长度。");
            string prefix=Path.GetFullPath(ProjectPath).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            CalibrationRawReference[] raw=previous!=null?previous.RawFiles:(source.RawPaths??new string[0]).Select(p=>{
                string full=Path.GetFullPath(p);
                if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new IOException("测距原始记录不属于当前项目。");
                return new CalibrationRawReference{RelativePath=full.Substring(prefix.Length),Sha256=LiveCalibrationRecord.HashFile(full)};
            }).ToArray();
            var wave=phase.Waveforms["TDR"];
            TdrAutoAnalysisResult analysis;
            if(!phase.Analyses.TryGetValue("TDR",out analysis)||analysis.SearchStatus!=TdrSearchStatus.Ok)
                throw new InvalidOperationException("请先确认有效的入射与反射波，尚未保存电缆长度。");
            double length=(marker.DistanceMarkerSeconds-marker.StartTimeSeconds)*phase.VelocityMPerUs*1e6/2;
            var record=new LiveCalibrationRecord{Version=3,Phase=SelectedPhaseCode,Level="TDR",MeasurementKind="Cable",
                DeviceSerial=source.DeviceSerial,DeviceModel=source.DeviceModel,RangeIndex=source.RangeIndex,
                SampleRateHz=source.SampleRateHz,AcquisitionMode=source.AcquisitionMode,PulseCount=previous!=null?previous.PulseCount:source.Windows.Length,
                PicoCoupling=source.PicoCoupling,DcRemoval=source.DcRemoval,ShortCable=source.ShortCable,FirstArrival=UseFirstArrivalTiming,
                FilterId=PdLinearHighPass.FilterId,CoefficientProcessing="HP10K_NO_LP",CoefficientPcPerV=Double.NaN,ChargePc=Double.NaN,
                BandwidthMHz=phase.WideBand?phase.BandwidthCutoffMHz:0,LowPassFilterId=phase.WideBand?PdLinearLowPass.FilterId:"",
                IncidentSeconds=marker.StartTimeSeconds,ReflectionSeconds=marker.DistanceMarkerSeconds,
                IncidentPeakIndex=analysis.IncidentIndex,
                ReflectionPeakIndex=analysis.ReflectionIndex,
                LengthM=length,VelocityMPerUs=phase.VelocityMPerUs,ReflectionStatus="Detected",VelocitySource="Configured",
                ReflectionSelectionMethod=ReflectionSelectionNoLock(phase,"TDR",false)?.Confirmed==true?"Manual":"Automatic",
                SnrDb=source.Assessment==null?Double.NaN:source.Assessment.SnrDb,
                AttenuationDb=Double.NaN,Correlation=source.Reflection==null?Double.NaN:source.Reflection.Correlation,
                TriggerThresholdV=source.ThresholdV,CreatedUtc=DateTime.UtcNow.ToString("o"),ProjectHash=LiveCalibrationRecord.Identity(ProjectPath),
                Average=(double[])wave.Samples.Clone(),RawFiles=raw};
            if(record.ReflectionSelectionMethod=="Manual") {
                record.PeakAnchoredSelection=ReflectionSelectionNoLock(phase,"TDR",false).PeakAnchored;
                record.ManualIncidentSeconds=record.PeakAnchoredSelection?wave.X0Seconds+analysis.IncidentIndex*wave.DxSeconds:marker.StartTimeSeconds;
                record.ManualReflectionSeconds=record.PeakAnchoredSelection?wave.X0Seconds+analysis.ReflectionIndex*wave.DxSeconds:marker.DistanceMarkerSeconds;
            }
            // Reject invalid waveform/provenance before changing project geometry.
            record.Validate(ProjectPath,true);return record;
        }
    }
}
