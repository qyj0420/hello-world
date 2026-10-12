using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;
using Xiongxin.ZeroBench;

namespace HVX.Controls
{
    // A recorded request to repeat an already-started plan round, NOT data
    // acceptance, a device-release proof or permission to energize hardware.
    public sealed class PressureOverflowRetryRecord
    {
        public string Schema="HVX.Pressure.OverflowUpscale/1",Purpose="AutomaticPlanOverflowUpscale";
        public string AcquisitionId,PlanContext,RawSha256,QualitySha256,OriginalQualitySha256;
        public string CalibrationSource,CalibrationSourceHash,Phase,ArtSerial,PicoSerial;
        public long RequestSequence,CompletedActionSequence,BackendGeneration;
        public int CurrentRangeIndex,NextRangeIndex,RetryOrdinal,MaximumRetries;
        public double TargetPeakKv,CalibrationPcPerVolt,NextRangePc;
        public DateTime CapturedUtc,RecordedUtc;
        public DateTimeOffset CalibrationUtc;
        public bool StartedPlanIntent,DataAccepted=false,ProvesHighVoltageSafe=false,StartsHardware=false;
        public LocationRangeResult Quality,OriginalQuality;
    }

    internal static class PressureOverflowUpscale
    {
        internal static bool HasActualOverflow(BenchCapture capture,LocationRangeResult q)
        {
            if(capture==null||capture.Settings==null||capture.Simulation||!capture.Settings.RealHardware||
                capture.PdAdc==null||capture.PdAdc.Length==0||capture.MaximumAdc<=0)return false;
            if(capture.Overflow)return true;
            // A serialized recommendation alone is not actual saturation evidence.
            foreach(short value in capture.PdAdc)if(Math.Abs((int)value)>=capture.MaximumAdc)return true;
            return false;
        }
        internal static int Next(double[] calibratedPc,int current,ISet<int> visited,int retries)
        {
            return NextMeasured(calibratedPc,current,visited,retries,current+1,0,0,0);
        }
        internal static int NextMeasured(double[] calibratedPc,int current,ISet<int> visited,int retries,
            int recommended,int sampleCount,int clippedSamples,int longestClippedRun)
        {
            return HVX.Acquisition.BoundedClippedRangePolicy.NextCalibrated(calibratedPc,current,recommended,
                visited,retries,true,sampleCount,clippedSamples,longestClippedRun);
        }
        internal static bool HasWindowOverflow(BenchCapture capture,LocationRangeResult q)
        {
            int clipped,run;
            return TryMeasureWindowClipping(capture,q,out clipped,out run)&&clipped>0;
        }
        internal static bool TryMeasureWindowClipping(BenchCapture capture,LocationRangeResult q,out int clipped,out int longestRun)
        {
            clipped=longestRun=0;
            if(capture==null||capture.Settings==null||capture.Simulation||!capture.Settings.RealHardware||
                capture.PdAdc==null||capture.PdAdc.Length==0||capture.MaximumAdc<=0||
                q==null||!q.WindowValid||!q.FactoryCycleWindow||
                q.WindowStartSample<0||q.WindowEndExclusive<=q.WindowStartSample||
                q.WindowEndExclusive>capture.PdAdc.Length||q.LocationSampleCount!=q.WindowEndExclusive-q.WindowStartSample||
                q.FullSampleCount!=capture.PdAdc.Length)return false;
            // A full-segment flag alone does not identify clipping in the selected
            // fixed-cycle window. Unknown overflow stays a separate quality hold.
            int run=0;
            for(int i=q.WindowStartSample;i<q.WindowEndExclusive;i++)
                if(Math.Abs((int)capture.PdAdc[i])>=capture.MaximumAdc){clipped++;longestRun=Math.Max(longestRun,++run);}
                else run=0;
            return true;
        }
        internal static bool ProvenOutsideCycleClipping(BenchCapture capture,LocationRangeResult q)
        {
            return capture!=null&&capture.PdAdc!=null&&q!=null&&q.FactoryCycleWindow&&q.WindowValid&&
                q.OutsideCycleClippingExcluded&&q.LocationClippedSamples==0&&q.FullClippedSamples>0&&
                q.FullClippedSamples==q.OutsideClippedSamples&&q.FullSampleCount==capture.PdAdc.Length&&
                q.WindowStartSample>=0&&q.WindowEndExclusive>q.WindowStartSample&&q.WindowEndExclusive<=capture.PdAdc.Length&&
                q.LocationSampleCount==q.WindowEndExclusive-q.WindowStartSample;
        }
        static bool Finite(double v){return !Double.IsNaN(v)&&!Double.IsInfinity(v);}
        internal static string Xml(LocationRangeResult quality)
        {using(var text=new StringWriter(CultureInfo.InvariantCulture)){new XmlSerializer(typeof(LocationRangeResult)).Serialize(text,quality);return text.ToString();}}
        internal static LocationRangeResult Copy(LocationRangeResult quality)
        {if(quality==null)return null;using(var text=new StringReader(Xml(quality)))return (LocationRangeResult)new XmlSerializer(typeof(LocationRangeResult)).Deserialize(text);}
        internal static string QualityHash(LocationRangeResult q){return Hash(Encoding.UTF8.GetBytes(Xml(q)));}
        internal static string Hash(byte[] bytes)
        {using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");}
        internal static string FileHash(string path)
        {using(var sha=SHA256.Create())using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");}
    }

    public sealed partial class PressureTestControl
    {
        bool CanAutomaticallyUpscaleOverflow(PressureZeroVoltageResult source,int current,out int next)
        {
            next=-1;int clipped,longestRun;
            if(!automaticRange||advancedMode||!planMode||SimulationMode||!HasDischargeCalibrationNoLock()||
                !cycleIdentityBound||!cycleIdentityAccepted||cycleTargetPeakKv!=requestedTargetPeakVoltageKv||
                cyclePhase!=SelectedPhaseCode||!String.Equals(cycleProject,ProjectPath,StringComparison.OrdinalIgnoreCase)||cycleSimulation!=SimulationMode||
                source==null||source.Quality==null||!source.Quality.WindowValid||source.Capture==null||
                source.Capture.Settings==null||source.Capture.Settings.RangeIndex!=current||
                source.Capture.Settings.PdCalibrationPcPerV!=calibrationPcPerVolt||
                source.Capture.Settings.AutoRange!=automaticRange||!HasActualReleasedPressureCapture()||
                !PressureOverflowUpscale.TryMeasureWindowClipping(source.Capture,source.Quality,out clipped,out longestRun)||clipped==0)return false;
            var q=source.Quality;
            if(q.WindowStartSample<0||q.LocationSampleCount<=0||q.WindowEndExclusive<=q.WindowStartSample||
                (long)q.WindowStartSample+q.LocationSampleCount!=q.WindowEndExclusive||
                q.WindowEndExclusive>source.Capture.PdAdc.Length||q.FullSampleCount!=source.Capture.PdAdc.Length)return false;
            var original=source.OriginalRangeQuality??source.Quality;
            next=PressureOverflowUpscale.NextMeasured(GetExactDischargeRangesPc(),current,PressurePlanVisitedRangesNoLock(),pressureRangeRetries,
                original.RecommendedIndex,q.LocationSampleCount,clipped,longestRun);
            return next>current;
        }
        bool HasActualReleasedPressureCapture()
        {
            // The configured delegate already promises completion only after
            // native release. Require the existing unique action claimant's ACK,
            // not merely UI COMPLETE or a manually installed waveform.
            lock(zeroHardwareLock)return pressureHardwareCapture!=null&&zeroActionOwner==null&&!zeroHardwareRunning&&
                zeroLastClaimedActionSequence>0&&zeroLastClaimedActionSequence==ActionSequence&&
                LastActionSucceeded&&ActionCode=="PRESSURE"&&!ActionPending&&String.IsNullOrEmpty(LastActionError);
        }
        ReviewedRangeRetry CreateOverflowRetry(PressureZeroVoltageResult source,string id,long request,string context,int current,int next)
        {
            var original=source.OriginalRangeQuality??source.Quality;
            var record=new PressureOverflowRetryRecord{AcquisitionId=id,RequestSequence=request,PlanContext=context,
                CompletedActionSequence=ActionSequence,BackendGeneration=zeroHardwareGeneration,
                CurrentRangeIndex=current,NextRangeIndex=next,RetryOrdinal=pressureRangeRetries+1,MaximumRetries=BenchSettings.Ranges.Length-1,
                TargetPeakKv=requestedTargetPeakVoltageKv,StartedPlanIntent=true,RawSha256=source.RawSha256,
                QualitySha256=PressureOverflowUpscale.QualityHash(source.Quality),OriginalQualitySha256=PressureOverflowUpscale.QualityHash(original),
                Quality=PressureOverflowUpscale.Copy(source.Quality),OriginalQuality=PressureOverflowUpscale.Copy(original),
                CalibrationSource=calibrationSource,CalibrationSourceHash=calibrationSourceHash,CalibrationUtc=calibrationAcquiredUtc,
                CalibrationPcPerVolt=calibrationPcPerVolt,NextRangePc=GetExactDischargeRangesPc()[next],Phase=SelectedPhaseCode,
                CapturedUtc=source.Capture.CapturedUtc,ArtSerial=source.Capture.ArtSerial,PicoSerial=source.Capture.PicoSerial,RecordedUtc=DateTime.UtcNow};
            return new ReviewedRangeRetry{Context=context,Acquisition=id,Request=request,Next=next,StartedIntent=true,
                Overflow=record,Source=source,Action=ActionSequence,Generation=zeroHardwareGeneration,
                EvidencePath=Path.Combine(source.Directory,"pressure-overflow-retry-"+Guid.NewGuid().ToString("N")+".xml")};
        }
        void WriteOverflowRetry(ReviewedRangeRetry permit)
        {
            string dir=PressureZeroVoltageCapture.CaptureDirectory(ProjectPath,permit.Source.Directory);
            string raw=Path.Combine(dir,"capture.bin");
            if(!String.Equals(PressureOverflowUpscale.FileHash(raw),permit.Overflow.RawSha256,StringComparison.Ordinal))
                throw new InvalidDataException("本轮原始数据哈希不一致，不能自动重采。");
            // No overwrites of the source quality or raw record.
            using(var file=new FileStream(permit.EvidencePath,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                new XmlSerializer(typeof(PressureOverflowRetryRecord)).Serialize(file,permit.Overflow);
            permit.EvidenceHash=PressureOverflowUpscale.FileHash(permit.EvidencePath);
            var info=new FileInfo(raw);permit.RawLength=info.Length;permit.RawWriteTicks=info.LastWriteTimeUtc.Ticks;
        }
        bool OverflowRetryCurrent(ReviewedRangeRetry permit)
        {
            var r=permit.Overflow;int next;
            if(r==null||!Object.ReferenceEquals(lastPressureCapture,permit.Source)||lastPressureAcquisitionId!=permit.Acquisition||
                !HasRecordedRangeRetryStartIntent(permit.Request)||permit.Action!=ActionSequence||permit.Generation!=zeroHardwareGeneration||
                SelectedPicoRangeCode!=r.CurrentRangeIndex||!CanAutomaticallyUpscaleOverflow(permit.Source,r.CurrentRangeIndex,out next)||next!=permit.Next||
                r.CalibrationSourceHash!=calibrationSourceHash||r.QualitySha256!=PressureOverflowUpscale.QualityHash(permit.Source.Quality)||
                r.OriginalQualitySha256!=PressureOverflowUpscale.QualityHash(permit.Source.OriginalRangeQuality??permit.Source.Quality))return false;
            try {
                string dir=PressureZeroVoltageCapture.CaptureDirectory(ProjectPath,permit.Source.Directory);
                var raw=new FileInfo(Path.Combine(dir,"capture.bin"));
                return raw.Length==permit.RawLength&&raw.LastWriteTimeUtc.Ticks==permit.RawWriteTicks&&
                    !String.IsNullOrEmpty(permit.EvidenceHash)&&permit.EvidenceHash==PressureOverflowUpscale.FileHash(permit.EvidencePath);
            }catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}
        }
    }
}
