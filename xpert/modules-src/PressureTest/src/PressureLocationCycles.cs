using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Xiongxin.OWTS.Data;

namespace HVX.Controls
{
    public sealed partial class PressureTestControl
    {
        int locationSaveCycleCount=1;
        // Match the existing LOC reader's pC x 100000 contract, but store an
        // integer rather than a round-trip formatted floating-point number.
        // Reject non-finite/overflow input instead of silently clipping it.
        internal static string EncodeLocationCharge(double samplePc)
        {
            double scaled=samplePc*100000d;
            if(!IsFinite(scaled))throw new InvalidDataException("定位数据含无效放电量，未保存。");
            long value=checked((long)Math.Round(scaled,MidpointRounding.AwayFromZero));
            return value.ToString(CultureInfo.InvariantCulture);
        }
        bool cyclePressureAttempt,cycleWaveformSeen,cycleFailureNotified;
        OscillationCycleWindow locationCycleWindow;
        string locationCycleFailure="";
        double cycleRawCapacitance,cycleDisplayedCapacitance,cycleCompensation;
        bool cycleUseCompensation;
        string cyclePhase="",cycleProject="";
        bool cycleSimulation;
        double cycleTargetPeakKv,cycleCableLength;
        bool cycleIdentityBound,cycleIdentityAccepted;
        string cycleAcquisitionId="",cycleReceivedUtc="";
        string locationReferenceQueuedId="";
        Task locationReferenceWriteTask;
        [Browsable(false)] public string LocationAcquisitionId{get{return cycleAcquisitionId;}}
        public event EventHandler AcquisitionValidationFailed;
        public int LocationSaveCycleCount {
            get{return locationSaveCycleCount;}
            set{if(value<1||value>5)throw new ArgumentOutOfRangeException("value");RunOnUiThread(delegate{
                if(value==locationSaveCycleCount)return;
                if(cyclePressureAttempt||PressureParametersLocked)throw new InvalidOperationException("Do not change saved cycle count during an acquisition or pending save.");
                locationSaveCycleCount=value;locationCycleWindow=null;cycleWaveformSeen=false;
            });}
        }
        [Browsable(false)] public string LocationCycleStatusCode{get{return locationCycleWindow==null?"NOT_EVALUATED":locationCycleWindow.Success?"COMPLETE_CYCLES":locationCycleWindow.FailureCode;}}
        [Browsable(false)] public string LocationCycleFailureReason{get{return locationCycleFailure;}}
        [Browsable(false)] public int LocationActualCycleCount{get{return locationCycleWindow!=null&&locationCycleWindow.Success?locationCycleWindow.CycleCount:0;}}
        void ResetLocationCycleAcquisition()
        {cyclePressureAttempt=false;cycleWaveformSeen=false;cycleFailureNotified=false;locationCycleWindow=null;locationCycleFailure="";cycleIdentityBound=false;cycleIdentityAccepted=false;cycleAcquisitionId="";}
        void BeginLocationCycleAcquisition()
        {
            cycleIdentityBound=true;cycleAcquisitionId=Guid.NewGuid().ToString("N");
            cyclePhase=SelectedPhaseCode;cycleProject=ProjectPath;cycleSimulation=SimulationMode;
            cycleTargetPeakKv=requestedTargetPeakVoltageKv;cycleCableLength=CableLengthMeters;
            cycleZeroAnalysisRequested=analyzeZeroVoltageData&&cycleTargetPeakKv==0;
        }
        void ValidateLocationAcquisitionIdentity(IDictionary<string,object> metadata)
        {
            object token=null; bool hasToken=metadata!=null&&metadata.TryGetValue("locationAcquisitionId",out token);
            if(hasToken&&(!cycleIdentityBound||!String.Equals(Convert.ToString(token,CultureInfo.InvariantCulture),cycleAcquisitionId,StringComparison.Ordinal)))
                throw new InvalidOperationException("Stale acquisition identity; waveform was not installed.");
            if(cycleIdentityBound&&(cyclePhase!=SelectedPhaseCode||!String.Equals(cycleProject,ProjectPath,StringComparison.OrdinalIgnoreCase)||cycleSimulation!=SimulationMode))
                throw new InvalidOperationException("Acquisition context changed after pressure request; waveform was not installed.");
            cycleIdentityAccepted=hasToken&&cycleIdentityBound;
            cycleReceivedUtc=DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture);
        }
        void EvaluateLocationCycleWindow()
        {
            if(currentZeroVoltageAnalysis){locationCycleWindow=null;cycleWaveformSeen=true;locationCycleFailure="";return;}
            if(currentAcquisitionReferenceOnly||currentAcquisitionReferencePreview){locationCycleWindow=null;cycleWaveformSeen=false;return;}
            cycleWaveformSeen=true;
            // Real hardware streams have independent rates; only a matching
            // acquisition may supply the Pico clock. The external waveform API
            // retains its explicit common-rate contract, not an inferred clock.
            bool captured=lastPressureCapture!=null&&lastPressureCapture.Capture!=null&&
                lastPressureAcquisitionId==cycleAcquisitionId&&Object.ReferenceEquals(lastPressureCapture.ChargePc,lastDischargePc);
            double pdRate=captured?lastPressureCapture.Capture.PicoRateHz:lastSampleRateHz;
            locationCycleWindow=OscillationCycleWindow.FindUpTo(lastVoltageKv,lastSampleRateHz,locationSaveCycleCount,
                lastDischargePc.Length,pdRate,0,cyclePressureAttempt?requestedTargetPeakVoltageKv:0);
            // The setting is a maximum; retain only actual complete cycles.
            // Whole-ms display extent never replaces this storage window.
            locationCycleFailure=locationCycleWindow.Success?"":locationCycleWindow.FailureReason;
            cycleRawCapacitance=RawEquivalentCapacitanceUf;cycleDisplayedCapacitance=DisplayedEquivalentCapacitanceUf;
            cycleUseCompensation=UseCompensationCapacitance;cycleCompensation=CompensationCapacitanceUf;
        }
        bool RejectIncompleteLocationAcquisition()
        {
            if(currentZeroVoltageAnalysis&&cycleIdentityAccepted&&cycleTargetPeakKv==0){cyclePressureAttempt=false;return false;}
            if(currentAcquisitionReferenceOnly||currentAcquisitionReferencePreview){cyclePressureAttempt=false;return false;}
            // State-only legacy notifications remain supported. New pressure requests
            // and supplied waveforms must have evidence of complete cycles.
            if(!cyclePressureAttempt&&!cycleWaveformSeen)return false;
            if(locationCycleWindow!=null&&locationCycleWindow.Success){cyclePressureAttempt=false;return false;}
            QueueIncompleteLocationRawReference();
            string reason=String.IsNullOrEmpty(locationCycleFailure)?PressureUiText("未收到本次电压参考波形","No voltage reference was received for this acquisition"):locationCycleFailure;
            locationCycleFailure=reason;
            RecordPressureDiagnostic("IncompleteLocationAcquisition",reason);
            string message=PressureUiText("采集数据不完整，本轮未保存为有效试验。请按现场规程确认高压停止，再检查接线和采集设置。",
                "Acquisition data are incomplete; this round was not saved as a valid test. Verify high voltage is stopped under site procedures, then check the wiring and acquisition settings.");
            lock(stateLock){dataSaved=false;dataAnalyzed=false;deleteEnabled=false;lastPostProcessError=lastError=message;
                lastActionCode="ACQUISITION_CYCLE_INVALID";SetPostProcessStateNoLock("ACQUISITION_INVALID");pressureStateCode="FAULT";}
            CancelAutomaticPressureSchedulingCore();UpdatePressureUi();
            if(!cycleFailureNotified){cycleFailureNotified=true;
                Exception notificationError=null;
                try{Raise(PostProcessStateChanged);}catch(Exception ex){notificationError=ex;}
                try{Raise(AcquisitionValidationFailed);}catch(Exception ex){if(notificationError==null)notificationError=ex;}
                if(IsHandleCreated&&Visible&&FindForm()!=null&&FindForm().Visible)
                    MessageBox.Show(FindForm(),message,PressureUiText("加压／采集异常","Acquisition warning"),MessageBoxButtons.OK,MessageBoxIcon.Warning);
                if(notificationError!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(notificationError).Throw();}
            return true;
        }

        PressureLocationReferenceContext CaptureLocationRawReferenceContext(PressureZeroVoltageResult source,string id)
        {
            if(!cycleIdentityAccepted||!cycleIdentityBound||source==null||source.Capture==null||
                !Object.ReferenceEquals(source,lastPressureCapture)||!Object.ReferenceEquals(source.ChargePc,lastDischargePc)||
                id!=cycleAcquisitionId||id!=lastPressureAcquisitionId||cycleTargetPeakKv<=0||
                cyclePhase!=SelectedPhaseCode||cycleSimulation!=SimulationMode||
                !String.Equals(cycleProject,ProjectPath,StringComparison.OrdinalIgnoreCase))return null;
            return PressureLocationReferenceRetention.Create(source,cycleProject,id,cyclePhase,locationSaveCycleCount,locationCycleWindow);
        }
        bool LocationRawReferenceOwnerIsCurrent(PressureLocationReferenceContext value)
        {
            bool current=false;
            RunOnUiThread(()=>current=LocationRawReferenceOwnerIsCurrentOnUi(value));
            return current;
        }
        bool LocationRawReferenceOwnerIsCurrentOnUi(PressureLocationReferenceContext value)
        {
            return value!=null&&cycleIdentityAccepted&&cycleIdentityBound&&
                value.AcquisitionId==cycleAcquisitionId&&value.AcquisitionId==lastPressureAcquisitionId&&
                Object.ReferenceEquals(value.Source,lastPressureCapture)&&Object.ReferenceEquals(value.Window,locationCycleWindow)&&
                cycleSimulation==SimulationMode&&value.Phase==SelectedPhaseCode&&String.Equals(value.Project,ProjectPath,StringComparison.OrdinalIgnoreCase);
        }
        void PublishLocationRawReference(PressureLocationReferenceContext value)
        {
            PressureLocationReferenceRetention.Publish(value,()=>LocationRawReferenceOwnerIsCurrent(value),commit=>{
                bool published=false;
                RunOnUiThread(()=>{
                    if(!LocationRawReferenceOwnerIsCurrentOnUi(value))throw new InvalidOperationException("Raw reference owner changed before commit.");
                    commit();published=true;
                });
                if(!published)throw new InvalidOperationException("Raw reference owner was disposed before commit.");
            });
        }
        void QueueIncompleteLocationRawReference()
        {
            // Failure already retains raw. Metadata is best-effort and cannot ACK,
            // unblock an invalid round or delay its stop/validation notification.
            if(locationReferenceQueuedId==cycleAcquisitionId)return;
            PressureLocationReferenceContext context;
            try{context=CaptureLocationRawReferenceContext(lastPressureCapture,lastPressureAcquisitionId);}
            catch(Exception error)when(error is IOException||error is InvalidOperationException||error is ArgumentException){
                System.Diagnostics.Trace.TraceWarning("Location raw reference retained without metadata: "+error.Message);return;}
            if(context==null||!context.Retain)return;
            locationReferenceQueuedId=context.AcquisitionId;
            locationReferenceWriteTask=Task.Run(()=>{
                try{PublishLocationRawReference(context);}
                catch(Exception error)when(error is IOException||error is UnauthorizedAccessException||error is InvalidOperationException||error is ArgumentException){
                    System.Diagnostics.Trace.TraceWarning("Location raw reference retained without metadata: "+error.Message);}
            });
        }
        sealed class LocationSaveContext
        {
            internal string Id,Project,Phase,Path,ReceivedUtc;
            internal bool Simulation,UseCompensation;
            internal int MaximumCycles;
            internal double TargetPeak,CableLength,RawCapacitance,DisplayedCapacitance,Compensation;
            internal OscillationCycleWindow Window,OriginalWindow;
            internal Dictionary<string,object> QualityMetadata;
        }

        bool LocationSaveContextIsCurrent(LocationSaveContext value)
        {
            return !operatorPressureStopRequested&&value!=null&&cycleIdentityAccepted&&cycleIdentityBound&&
                String.Equals(value.Id,cycleAcquisitionId,StringComparison.Ordinal)&&
                String.Equals(value.Project,ProjectPath,StringComparison.OrdinalIgnoreCase)&&
                value.Phase==SelectedPhaseCode&&value.Simulation==SimulationMode&&
                Object.ReferenceEquals(value.OriginalWindow,locationCycleWindow);
        }

        public Task<bool> SaveLocationCycleDataAsync(string acquisitionId,string filePath,double[] locationChargePc,double locationSampleRateHz,double locationStreamStartSeconds,double velocityMPerUs)
        {return SaveLocationCycleDataAsync(acquisitionId,filePath,locationChargePc,locationSampleRateHz,locationStreamStartSeconds,velocityMPerUs,CancellationToken.None);}

        // acquisitionId must come from the PD callback, not from querying the current ID later.
        // The caller retains an immutable PD buffer until this task completes. Await this API;
        // do not block the UI thread with Task.Result/Wait. Only context capture and commit use UI.
        public Task<bool> SaveLocationCycleDataAsync(string acquisitionId,string filePath,double[] locationChargePc,double locationSampleRateHz,double locationStreamStartSeconds,double velocityMPerUs,CancellationToken cancellationToken)
        {return SaveLocationCycleDataAsync(acquisitionId,filePath,locationChargePc,locationSampleRateHz,locationStreamStartSeconds,velocityMPerUs,String.Empty,cancellationToken);}

        public Task<bool> SaveLocationCycleDataAsync(string acquisitionId,string filePath,double[] locationChargePc,double locationSampleRateHz,double locationStreamStartSeconds,double velocityMPerUs,string acquisitionFilterId,CancellationToken cancellationToken)
        {
            if(currentZeroVoltageAnalysis)return SaveZeroVoltageLocationDataAsync(acquisitionId,filePath,locationChargePc,locationSampleRateHz,locationStreamStartSeconds,velocityMPerUs,acquisitionFilterId,cancellationToken);
            LocationSaveContext context=null;
            RunOnUiThread(delegate {try {
                cancellationToken.ThrowIfCancellationRequested();
                if(operatorPressureStopRequested)throw new InvalidOperationException("本轮已中断，仅供显示，不保存定位数据。");
                if(acquisitionFilterId==null || (acquisitionFilterId.Length>0 && acquisitionFilterId!=HVX.SignalProcessing.PdLinearHighPass.FilterId))
                    throw new InvalidDataException("Unknown acquisition high-pass filter; LOC was not saved.");
                if(!IsHandleCreated)throw new InvalidOperationException("Initialize the pressure-test UI before asynchronous saving.");
                if(currentAcquisitionReferenceOnly||currentAcquisitionReferencePreview)throw new InvalidOperationException("Noise / reference previews are not PD location records.");
                if(locationCycleWindow==null||!locationCycleWindow.Success)throw new InvalidDataException("No validated complete-cycle voltage reference is available.");
                if(!cycleIdentityAccepted)throw new InvalidOperationException("Saving requires waveform metadata with the current locationAcquisitionId; legacy unbound previews cannot be saved as new LOC records.");
                if(String.IsNullOrEmpty(acquisitionId)||!String.Equals(acquisitionId,cycleAcquisitionId,StringComparison.Ordinal))
                    throw new InvalidOperationException("The PD buffer acquisitionId is stale or missing; location data was not saved.");
                if(cyclePhase!=SelectedPhaseCode||!String.Equals(cycleProject,ProjectPath,StringComparison.OrdinalIgnoreCase)||cycleSimulation!=SimulationMode)
                    throw new InvalidOperationException("Acquisition project, phase or simulation mode changed; do not save under the new context.");
                if(!IsFinite(velocityMPerUs)||velocityMPerUs<=0||velocityMPerUs>299.792458)throw new ArgumentOutOfRangeException("velocityMPerUs");
                if(String.IsNullOrWhiteSpace(ProjectPath))throw new InvalidOperationException("A project is required.");
                string root=Path.GetFullPath(ProjectPath),full=Path.GetFullPath(filePath);
                if(!Directory.Exists(root)||!String.Equals(Path.GetDirectoryName(full),root.TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase)||!String.Equals(Path.GetExtension(full),".loc",StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("LOC destination must be a new file in the current project.");
                Match identity=Regex.Match(Path.GetFileName(full),@"^(A|B|C)_([0-9]+(?:\.[0-9]+)?)kV_",RegexOptions.IgnoreCase);double namedVoltage;
                if(!identity.Success||!String.Equals(identity.Groups[1].Value,SelectedPhaseCode,StringComparison.OrdinalIgnoreCase)||
                    !Double.TryParse(identity.Groups[2].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out namedVoltage)||Math.Abs(namedVoltage-cycleTargetPeakKv)>.005)
                    throw new ArgumentException("LOC identity must match acquired phase and requested peak voltage.");
                if(File.Exists(full))throw new IOException("The destination LOC already exists; it will not be overwritten.");
                context=new LocationSaveContext {Id=acquisitionId,Project=root,Phase=cyclePhase,Path=full,ReceivedUtc=cycleReceivedUtc,
                    Simulation=cycleSimulation,UseCompensation=cycleUseCompensation,Compensation=cycleCompensation,MaximumCycles=locationSaveCycleCount,
                    TargetPeak=cycleTargetPeakKv,CableLength=cycleCableLength,RawCapacitance=cycleRawCapacitance,DisplayedCapacitance=cycleDisplayedCapacitance,
                    OriginalWindow=locationCycleWindow,
                    QualityMetadata=CapturePressureQualityMetadata(lastPressureCapture,acquisitionId),
                    Window=new OscillationCycleWindow {CrossingSeconds=(double[])locationCycleWindow.CrossingSeconds.Clone(),FirstCrossingPositive=locationCycleWindow.FirstCrossingPositive,SavedDurationSeconds=locationCycleWindow.SavedDurationSeconds}};
            }catch(Exception ex){
                if(ex is OutOfMemoryException)throw;
                // A late callback must not replace the new acquisition/project's status.
                if(String.IsNullOrEmpty(acquisitionId)||(cycleIdentityBound&&String.Equals(acquisitionId,cycleAcquisitionId,StringComparison.Ordinal)&&
                    cyclePhase==SelectedPhaseCode&&String.Equals(cycleProject,ProjectPath,StringComparison.OrdinalIgnoreCase)&&cycleSimulation==SimulationMode))
                {lastError=lastPostProcessError=ex.Message;lastActionCode="LOCATION_CYCLES_SAVE_FAILED";}
            } });
            if(context==null)return Task.FromResult(false);
            return Task.Run(delegate {
                string temporary=null;
                try {
                    cancellationToken.ThrowIfCancellationRequested();
                    LocationCycleReference reference;
                    double[] samples=context.Window.Crop(locationChargePc,locationSampleRateHz,locationStreamStartSeconds,out reference);
                    reference.PdAcquisitionFilterId=acquisitionFilterId;
                    cancellationToken.ThrowIfCancellationRequested();
                    var details=new Dictionary<string,object>{{"simulation",context.Simulation},{"useCompensationCapacitance",context.UseCompensation},{"compensationCapacitanceUf",context.Compensation},
                        {"rawCapacitanceUf",IsFinite(context.RawCapacitance)?(object)context.RawCapacitance:null},{"displayedCapacitanceUf",IsFinite(context.DisplayedCapacitance)?(object)context.DisplayedCapacitance:null},
                        {"locationAcquisitionId",context.Id},{"savedUtc",DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)},
                        {"requestedMaximumCycles",context.MaximumCycles},{"actualCycleCount",reference.CycleCount}};
                    foreach(var quality in context.QualityMetadata)details[quality.Key]=quality.Value;
                    string[] headers={"HVX LOC cycle record / engineering pC x 100000","Received UTC\t"+context.ReceivedUtc,
                        "Phase\t"+reference.InitialPhaseDegrees.ToString("R",CultureInfo.InvariantCulture),"Target peak kV\t"+context.TargetPeak.ToString("R",CultureInfo.InvariantCulture),
                        "Cable-length\t"+context.CableLength.ToString("R",CultureInfo.InvariantCulture),"Velocity (m/us)\t"+velocityMPerUs.ToString("R",CultureInfo.InvariantCulture),
                        "Delta x [s]\t"+(1/locationSampleRateHz).ToString("R",CultureInfo.InvariantCulture),"Charge unit\tpC x 100000",
                        "Capture start seconds\t"+reference.CaptureStartSeconds.ToString("R",CultureInfo.InvariantCulture),"Acquisition context\t"+new JavaScriptSerializer().Serialize(details),reference.HeaderLine()};
                    temporary=Path.Combine(context.Project,".loc-"+Guid.NewGuid().ToString("N")+".tmp");
                    using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new StreamWriter(stream,new UTF8Encoding(true))){
                        foreach(string line in headers)writer.WriteLine(line);
                        for(int i=0;i<samples.Length;i++){
                            if((i&4095)==0)cancellationToken.ThrowIfCancellationRequested();
                            writer.WriteLine(EncodeLocationCharge(samples[i]));
                        }
                    }
                    bool saved=false;
                    RunOnUiThread(delegate {
                        cancellationToken.ThrowIfCancellationRequested();
                        if(!LocationSaveContextIsCurrent(context))throw new InvalidOperationException("Acquisition context changed while saving; location data was not committed.");
                        File.Move(temporary,context.Path);saved=true;
                        lastError=lastPostProcessError=String.Empty;lastActionCode="LOCATION_CYCLES_SAVED";
                    });
                    return saved;
                }catch(Exception ex){
                    if(ex is OutOfMemoryException)throw;
                    RunOnUiThread(delegate {if(LocationSaveContextIsCurrent(context)){lastError=lastPostProcessError=ex.Message;lastActionCode="LOCATION_CYCLES_SAVE_FAILED";}});
                    return false;
                }finally{if(temporary!=null&&File.Exists(temporary))File.Delete(temporary);}
            });
        }

        // Explicit acquisition-host API; shared capture clock, no hardware commands.
        // Existing files are never overwritten. Simulation is labelled, never disguised.
        public bool SaveLocationCycleData(string filePath,double[] locationChargePc,double locationSampleRateHz,double locationStreamStartSeconds,double velocityMPerUs)
        {
            bool saved=false;
            RunOnUiThread(delegate{try{
                if(operatorPressureStopRequested)throw new InvalidOperationException("本轮已中断，仅供显示，不保存定位数据。");
                if(currentAcquisitionReferenceOnly||currentAcquisitionReferencePreview)throw new InvalidOperationException("Noise / reference previews are not PD location records.");
                if(locationCycleWindow==null||!locationCycleWindow.Success)throw new InvalidDataException("No validated complete-cycle voltage reference is available.");
                if(!cycleIdentityAccepted)throw new InvalidOperationException("Saving requires waveform metadata with the current locationAcquisitionId; legacy unbound previews cannot be saved as new LOC records.");
                if(cyclePhase!=SelectedPhaseCode||!String.Equals(cycleProject,ProjectPath,StringComparison.OrdinalIgnoreCase)||cycleSimulation!=SimulationMode)
                    throw new InvalidOperationException("Acquisition project, phase or simulation mode changed; do not save under the new context.");
                if(!IsFinite(velocityMPerUs)||velocityMPerUs<=0||velocityMPerUs>299.792458)throw new ArgumentOutOfRangeException("velocityMPerUs");
                if(String.IsNullOrWhiteSpace(ProjectPath))throw new InvalidOperationException("A project is required.");
                string root=Path.GetFullPath(ProjectPath),full=Path.GetFullPath(filePath);
                if(!Directory.Exists(root)||!String.Equals(Path.GetDirectoryName(full),root.TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase)||!String.Equals(Path.GetExtension(full),".loc",StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("LOC destination must be a new file in the current project.");
                Match identity=Regex.Match(Path.GetFileName(full),@"^(A|B|C)_([0-9]+(?:\.[0-9]+)?)kV_",RegexOptions.IgnoreCase);double namedVoltage;
                if(!identity.Success||!String.Equals(identity.Groups[1].Value,SelectedPhaseCode,StringComparison.OrdinalIgnoreCase)||
                    !Double.TryParse(identity.Groups[2].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out namedVoltage)||Math.Abs(namedVoltage-cycleTargetPeakKv)>.005)
                    throw new ArgumentException("LOC identity must match acquired phase and requested peak voltage.");
                LocationCycleReference reference;
                double[] samples=locationCycleWindow.Crop(locationChargePc,locationSampleRateHz,locationStreamStartSeconds,out reference);
                var details=new Dictionary<string,object>{{"simulation",cycleSimulation},{"useCompensationCapacitance",cycleUseCompensation},{"compensationCapacitanceUf",cycleCompensation},
                    {"rawCapacitanceUf",IsFinite(cycleRawCapacitance)?(object)cycleRawCapacitance:null},{"displayedCapacitanceUf",IsFinite(cycleDisplayedCapacitance)?(object)cycleDisplayedCapacitance:null}};
                details["locationAcquisitionId"]=cycleAcquisitionId;details["savedUtc"]=DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture);
                details["requestedMaximumCycles"]=locationSaveCycleCount;details["actualCycleCount"]=reference.CycleCount;
                foreach(var quality in CapturePressureQualityMetadata(lastPressureCapture,cycleAcquisitionId))details[quality.Key]=quality.Value;
                string[] headers={"HVX LOC cycle record / engineering pC x 100000","Received UTC\t"+cycleReceivedUtc,
                    "Phase\t"+reference.InitialPhaseDegrees.ToString("R",CultureInfo.InvariantCulture),"Target peak kV\t"+cycleTargetPeakKv.ToString("R",CultureInfo.InvariantCulture),
                    "Cable-length\t"+cycleCableLength.ToString("R",CultureInfo.InvariantCulture),"Velocity (m/us)\t"+velocityMPerUs.ToString("R",CultureInfo.InvariantCulture),
                    "Delta x [s]\t"+(1/locationSampleRateHz).ToString("R",CultureInfo.InvariantCulture),"Charge unit\tpC x 100000",
                    "Capture start seconds\t"+reference.CaptureStartSeconds.ToString("R",CultureInfo.InvariantCulture),"Acquisition context\t"+new JavaScriptSerializer().Serialize(details),reference.HeaderLine()};
                string temporary=Path.Combine(root,".loc-"+Guid.NewGuid().ToString("N")+".tmp");
                try{using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new StreamWriter(stream,new UTF8Encoding(true))){
                    foreach(string line in headers)writer.WriteLine(line);foreach(double sample in samples)writer.WriteLine(EncodeLocationCharge(sample));}
                    File.Move(temporary,full);saved=true;
                }finally{if(File.Exists(temporary))File.Delete(temporary);}
                lastActionCode="LOCATION_CYCLES_SAVED";
            }catch(Exception ex){if(ex is OutOfMemoryException)throw;lastError=lastPostProcessError=ex.Message;lastActionCode="LOCATION_CYCLES_SAVE_FAILED";saved=false;}});
            return saved;
        }
    }
}
