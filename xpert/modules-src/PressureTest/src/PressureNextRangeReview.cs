using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;
using Xiongxin.ZeroBench;

namespace HVX.Controls
{
    [XmlRoot("UnifiedPressureRangeReview")]
    public sealed class PressureRangeReviewSnapshot
    {
        public string Schema;
        public LocationRangeResult Quality;
        public LocationRangeResult OriginalQuality;
        public int CurrentRangeIndex, NextRangeIndex;
        public bool CanApply, NeedsPrompt, NextRoundBlocked;
        public string BlockReason;
        internal static PressureRangeReviewSnapshot Read(string xml,int current)
        {
            if(String.IsNullOrWhiteSpace(xml)||xml.Length>65536)throw new InvalidDataException("Missing range review.");
            PressureRangeReviewSnapshot value;
            using(var text=new StringReader(xml))
            using(var reader=XmlReader.Create(text,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=65536}))
                value=(PressureRangeReviewSnapshot)new XmlSerializer(typeof(PressureRangeReviewSnapshot)).Deserialize(reader);
            if((value.Schema!="HVX.Pressure.RangeReview/R27"&&value.Schema!="HVX.Pressure.RangeReview/V1.0.30.9")||value.Quality==null||value.CurrentRangeIndex!=current||
                current<0||current>=BenchSettings.Ranges.Length||value.NextRangeIndex<0||value.NextRangeIndex>=BenchSettings.Ranges.Length)
                throw new InvalidDataException("Range review identity mismatch.");
            return value;
        }
    }
    public sealed class PressureNextRangeDecision
    {
        public string Schema="HVX.Pressure.NextRangeDecision/R27",AcquisitionId,RawSha256,Choice,DecisionSource;
        public DateTime DecidedUtc;
        public bool StartsHardware=false,ProvesDischargeSafe=false;
        public bool AutomaticRangeReminderEnabled;
        public int MinimumRetryFloor=-1;
        public string PlanRangeRowContext;
        public PressureRangeReviewSnapshot Review;
    }
    public sealed partial class PressureTestControl
    {
        string reviewedRangeAcquisitionId="";
        long reviewedRangeRequest;
        string reviewedPlanRangeRowContext="";
        bool reviewedRangeMayContinue;
        bool automaticRangeReminderEnabled;
        internal Func<PressureRangeReviewSnapshot,int> NextRangeChoiceForTests;
        string pressureRangeReviewStatus="",pressureRangeReviewDetails="",pressureRangeReviewForId="";
        public string LastPressureRangeReviewStatus {get{return pressureRangeReviewForId==cycleAcquisitionId?pressureRangeReviewStatus:"";}}
        public string LastPressureRangeReviewDetails {get{return pressureRangeReviewForId==cycleAcquisitionId?pressureRangeReviewDetails:"";}}
        void ShowPressureRangeReview(PressureRangeReviewSnapshot review,string status)
        {
            pressureRangeReviewForId=cycleAcquisitionId;pressureRangeReviewStatus=status;
            pressureRangeReviewDetails=(review==null?"":review.Quality.WindowDefinition+"\r\n"+review.Quality.Reason+"\r\n"+review.Quality.LocationQuality+"\r\n"+review.BlockReason);
            UpdatePostProcessUi();
        }

        public bool AutomaticRangeReminderEnabled
        {
            get { lock(stateLock)return automaticRangeReminderEnabled; }
            // A presentation preference, not a range setting or start permit.
            // Allow the host to restore its saved value even during a capture
            // when an atomic preference save fails.
            set { RunOnUiThread(()=>{lock(stateLock)automaticRangeReminderEnabled=value;}); }
        }
        void PersistOrdinaryRangePromptSuppression(bool suppress)
        {
            if(!suppress)return;
            RunOnUiThread(()=>{
                lock(stateLock)automaticRangeReminderEnabled=false;
                Raise(PressurePreferencesChanged);
            });
        }

        // Returns permission to PREPARE a next round, never permission to output.
        // The device server separately verifies an explicitly started batch or
        // an individual start; a range decision cannot authorize an output.
        public async Task<bool> ReviewNextPressureRangeAsync()
        {
            // Waiting on the existing DI owner is not a failed range decision.
            // In particular, do not invalidate a cached row floor while a
            // bounded same-session read is pending. This grants no output.
            if(!await WaitForPendingP04ReadAsync(System.Threading.CancellationToken.None))return false;
            var capture=lastPressureCapture;string id=lastPressureAcquisitionId,project=ProjectPath,phase=SelectedPhaseCode;
            if(capture==null||capture.RangeReview==null||id!=cycleAcquisitionId||ProjectPath!=issuedPlanProject||SelectedPhaseCode!=issuedPlanPhase||IsBusy||PressureParametersLocked)return false;
            if(reviewedRangeAcquisitionId==id){
                lock(stateLock){
                    bool decisionCurrent=reviewedRangeRequest==pressureRequestSequence&&reviewedPlanRangeRowContext==CapturePressurePlanRangeRowNoLock()&&
                        !finishCurrentRoundRequested&&!operatorPressureStopRequested&&!automaticPlanInterrupted&&!pressureEmergencyLatched&&
                        CanRequestVoltageNoLock(requestedTargetPeakVoltageKv);
                    if(!decisionCurrent){pressurePlanRangeFloor.Reset();reviewedRangeMayContinue=false;}
                    return decisionCurrent&&reviewedRangeMayContinue;
                }
            }
            var review=capture.RangeReview;int current=SelectedPicoRangeCode;
            if(current!=review.CurrentRangeIndex)return false;
            long request=pressureRequestSequence;string rowContext=CapturePressurePlanRangeRowNoLock();
            bool critical=review.NextRoundBlocked||!review.Quality.WindowValid||review.Quality.AtMaximumAndClipped||review.Quality.MaximumRangeInsufficient||
                PressureChargeRangeFloor.MustHoldAtMaximum(review.Quality,current);
            int choice=critical?0:1;
            bool reminder=AutomaticRangeReminderEnabled;
            int floor;lock(stateLock)floor=SavedPressurePlanRangeFloorNoLock(capture,current,critical);
            bool keepRetryFloor=!critical&&automaticRange&&review.CanApply&&floor>=0&&review.NextRangeIndex<floor;
            bool automaticApplied=!critical&&automaticRange&&review.CanApply&&!keepRetryFloor;
            // Automatic range only consumes the frozen review's permitted next
            // setting. Critical warnings still require an operator; neither path
            // performs acquisition or grants permission for another HV round.
            if(automaticApplied)choice=2;
            // Ordinary next-round advice is now non-modal. Invalid windows and
            // range limits stop scheduling; their evidence remains visible.
            ShowPressureRangeReview(review,critical?PressureUiText("量程评估未通过，后续轮次已停止：","Range review stopped subsequent rounds: ")+
                (String.IsNullOrWhiteSpace(review.BlockReason)?!review.Quality.WindowValid?review.Quality.WindowDefinition:review.Quality.LocationQuality:review.BlockReason):
                automaticApplied?PressureUiText("已采用下一轮推荐量程","Recommended range applied to the next round"):PressureUiText("保留当前量程","Current range retained"));
            if(choice<0||choice>2||choice==2&&!review.CanApply)throw new InvalidOperationException("Invalid next-range decision.");
            if(IsDisposed||Disposing||!Visible||ProjectPath!=project||SelectedPhaseCode!=phase||cycleAcquisitionId!=id||SelectedPicoRangeCode!=current||IsBusy||PressureParametersLocked||
                request!=pressureRequestSequence||rowContext!=CapturePressurePlanRangeRowNoLock()||finishCurrentRoundRequested||operatorPressureStopRequested||automaticPlanInterrupted||pressureEmergencyLatched||
                !CanRequestVoltageNoLock(requestedTargetPeakVoltageKv))return false;
            var record=new PressureNextRangeDecision{AcquisitionId=id,RawSha256=capture.RawSha256,DecidedUtc=DateTime.UtcNow,
                Review=review,Choice=choice==2?"ApplyNextRound":choice==1?"KeepCurrent":"StopQueue",
                DecisionSource=keepRetryFloor?"AutomaticPlanOverflowFloor":automaticApplied?"AutomaticRange":critical?"Blocked":"NoPrompt",
                MinimumRetryFloor=floor,PlanRangeRowContext=rowContext,AutomaticRangeReminderEnabled=reminder};
            string path=Path.Combine(capture.Directory,"next-range-decision-"+Guid.NewGuid().ToString("N")+".xml");
            await Task.Run(()=>{using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                new XmlSerializer(typeof(PressureNextRangeDecision)).Serialize(stream,record);});
            // Disk scheduling can cross the freshness threshold. Resume only
            // after a fresh read, still bound to the same request/project/row.
            if(!await WaitForPendingP04ReadAsync(System.Threading.CancellationToken.None))return false;
            if(IsDisposed||Disposing||!Visible||ProjectPath!=project||SelectedPhaseCode!=phase||cycleAcquisitionId!=id||SelectedPicoRangeCode!=current||IsBusy||PressureParametersLocked||
                request!=pressureRequestSequence||rowContext!=CapturePressurePlanRangeRowNoLock()||finishCurrentRoundRequested||operatorPressureStopRequested||automaticPlanInterrupted||pressureEmergencyLatched||
                !CanRequestVoltageNoLock(requestedTargetPeakVoltageKv))return false;
            reviewedRangeAcquisitionId=id;reviewedRangeRequest=request;reviewedPlanRangeRowContext=rowContext;reviewedRangeMayContinue=choice!=0&&!critical;
            if(choice==2){lock(stateLock){selectedRangeCode=RangeCodes[review.NextRangeIndex];rangeChangedSequence++;}UpdateRangeUi();}
            return reviewedRangeMayContinue;
        }
    }
}
