using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Xiongxin.ZeroBench;

namespace Xiongxin.FactoryTest
{
    internal sealed partial class FactoryMainForm
    {
        // Reads archived metadata and arrays only. Never constructs a backend,
        // calls a worker, saves to the record, or dispatches a hardware command.
        internal async Task SmokeSequenceReplay(string output,string recordedDirectory)
        {
            smokeMode=true; Directory.CreateDirectory(output);
            FactoryResult recorded;
            using(var stream=File.OpenRead(Path.Combine(recordedDirectory,"result.xml")))
                recorded=(FactoryResult)new XmlSerializer(typeof(FactoryResult)).Deserialize(stream);
            recorded.Capture=BenchCapture.Load(recordedDirectory);
            recorded.Capture.ValidateCapture();
            if(!recorded.CaptureOnlyAvailable() || recorded.Settings.Input.OscillationCircuitConnected)
                throw new Exception("Expected actual completed R25 capture with missing circuit confirmation");
            var request=recorded.Settings.Clone();
            ResetStages(); stageSettings=request; activeCaptureKind=FactoryRunKind.Pressure;
            activeCaptureDirectory=recordedDirectory; RefreshResidualStageLabels();
            foreach(var key in new[]{"INIT","AO_ZERO","SAFE","HV_ON","HV_DELAY","HV_UP","ARM","ARM_DELAY","HV_OFF","OPTICAL","WAIT_TRIGGER","READ"})
                Progress("STEP_"+key+"_PASSED","历史事件回放，不操作设备");
            Progress("EMERGENCY_RELEASED","历史记录事件，仅测试UI，不是实时许可");
            Progress("STEP_DISCHARGE_SKIPPED","原记录采后AI未启动");
            Progress("STEP_COMPLETE_UNVERIFIED","历史采集结束事件");
            await LoadCapturePreview(recordedDirectory,++capturePreviewGeneration,false);
            bool stopped=false;int deliveries=0,confirmations=0,reviews=0;
            try
            {
                await FactoryPressureSequence.Run(request,6,s=>{++deliveries;return Task.FromResult(recorded);},
                    (i,t,s)=>{++confirmations;return Task.FromResult(false);},null,CancellationToken.None,
                    (r,q)=>{++reviews;return Task.FromResult(false);});
            }
            catch(FactorySequenceStoppedException ex)
            {
                if(ex.CompletedRounds!=1 || ex.LastCompletedRound!=recorded)throw;
                stopped=true;SequenceStopped(ex);
            }
            SetBusy(false);FreezeEmergency();
            if(!stopped || deliveries!=1 || confirmations!=0 || reviews!=1 || stages["DISCHARGE"].Visible ||
                ((StageButton)stages["COMPLETE"]).StateText!="已结束" || stages["COMPLETE"].BackColor!=Color.Gainsboro ||
                roundFailureDetail!=null || !combined.HasWaveform || !rawPd.HasWaveform || latest!=null ||
                !status.Text.Contains("连续队列停止") || !emergencyRead ||
                recorded.Completed || recorded.ResidualSafe || !recorded.PostCaptureMonitoringSkipped || !recorded.AcquisitionCompleted)
                throw new Exception("Actual R25 replay restored residual/error tile or lost captured data");
            Text="R26 · R25历史记录只读回放 · 未操作硬件";
            await Task.Delay(100);SaveImage(Path.Combine(output,"r26-real-record-replay.png"));
            File.WriteAllText(Path.Combine(output,"sequence-replay.txt"),
                "R26_ACTUAL_R25_READONLY_REPLAY=PASS\nCOMPLETED_MEASUREMENT_RETAINED=True\nNEXT_QUEUE_STOPPED=True\n"+
                "MISSING_CIRCUIT_CONFIRMATION=True\nEND_RESIDUAL_TILE_HIDDEN=True\nCURRENT_END_IS_NEUTRAL=True\n"+
                "ARCHIVED_RESULT_FLAGS_UNCHANGED=True\nHARDWARE_ACCESSED=False\nRECORD_WRITES=0\nNATIVE_BACKENDS=0\nSOURCE="+recordedDirectory);
        }
    }
}
