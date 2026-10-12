using System;
using System.ComponentModel;
using System.Threading;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        volatile bool enableAiFeatures=true;
        volatile bool enableDefectRecognition=true;
        int aiFeatureGeneration;
        readonly object aiFeatureCommitGate=new object();

        [Category("AI Learning"),DefaultValue(true)]
        public bool EnableDefectRecognition
        {
            get{return enableDefectRecognition;}
            set
            {
                if(enableDefectRecognition==value)return;
                enableDefectRecognition=value;recognitionGeneration++;recognitionUiGeneration++;dischargeResult=null;
                if(recognitionUiTimer!=null)recognitionUiTimer.Stop();
                QueueDischargeIdentification();
            }
        }
        bool DefectRecognitionEnabled{get{return enableAiFeatures&&enableDefectRecognition;}}
        void RequireDefectRecognition()
        {
            RequireAiFeatures();
            if(!enableDefectRecognition)throw new InvalidOperationException(L("Defect recognition is disabled in HV DAC Xpert Plus Data.","缺陷识别已在 HV DAC Xpert Plus Data 中关闭。"));
        }

        // The host persists this setting; no competing module preference.
        [Category("AI Learning"),DefaultValue(true)]
        public bool EnableAiFeatures
        {
            get{return enableAiFeatures;}
            set
            {
                if(enableAiFeatures==value)return;
                lock(aiFeatureCommitGate){enableAiFeatures=value;Interlocked.Increment(ref aiFeatureGeneration);}
                recognitionGeneration++;recognitionUiGeneration++;dischargeResult=null;
                if(recognitionUiTimer!=null)recognitionUiTimer.Stop();
                if(!value&&aiLearningMode)
                {
                    aiLearningMode=false;learningEvaluationSequence=-1;
                    pdmap.ApproximateCablePositions=approximationBeforeLearning;
                    spectrum.AutomaticConclusions=automaticConclusionsBeforeLearning;
                    RefreshApproximationProjection();SyncThresholdUi();RefreshWorkspaceUi();
                }
                aiLearningStatus=value?L("AI features enabled.","AI 功能已启用。"):AiFeaturesDisabledMessage();
                learningStatusMessage=aiLearningStatus;
                spectrum.SetLearningActionsEnabled(value&&!recognitionBusy&&!IsAnalysisRunning);
                QueueDischargeIdentification();RefreshAiLearningUi();RefreshEvaluationUi();QueueAutomaticEvaluation();
            }
        }
        string AiFeaturesDisabledMessage(){return L("AI features are disabled in application settings.","AI 功能已在参数设置中关闭。");}
        void RequireAiFeatures(){if(!EnableAiFeatures)throw new InvalidOperationException(AiFeaturesDisabledMessage());}
        bool CanWriteAiSample(int generation)
        {return enableAiFeatures&&generation==Interlocked.CompareExchange(ref aiFeatureGeneration,0,0)&&!IsDisposed&&!Disposing;}
    }
}
