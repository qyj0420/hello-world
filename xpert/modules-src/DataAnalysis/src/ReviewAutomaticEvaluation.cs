using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using HVX.UnifiedSpectrum;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        Timer automaticEvaluationTimer;
        bool automaticEvaluationPending;
        int automaticEvaluationGeneration;
        string automaticEvaluationKey;
        string automaticEvaluationDataProject;
        double[,] automaticEvaluationSource;
        LabVIEWPdMap.PdMapControl.ReviewConnectorGeometry automaticEvaluationGeometry;

        sealed class EvaluationWork
        {
            internal double[,] Input,Source;
            internal int[] Rows,Projected;
            internal int Accepted,Invalid,Sequence,Generation,Body,Joint;
            internal double Voltage,Years;
            internal string Runtime,Key,Parameters,VoltageSource;
            internal bool English;
            internal LabVIEWPdMap.PdMapControl.ReviewConnectorGeometry Geometry;
        }
        [Browsable(false)] public bool AutomaticEvaluationPending {get{return automaticEvaluationPending;}}
        [Browsable(false)] public double LastEvaluationPreparationMilliseconds {get;private set;}
        [Browsable(false)] public double LastEvaluationWorkerMilliseconds {get;private set;}

        void InitializeAutomaticEvaluation()
        {
            automaticEvaluationTimer=new Timer{Interval=300};
            automaticEvaluationTimer.Tick+=(s,e)=>StartAutomaticEvaluation();
            ResultsChanged+=(s,e)=>QueueAutomaticEvaluation();
            AnalysisCompleted+=(s,e)=>QueueAutomaticEvaluation();
            AnalysisSettingsChanged+=(s,e)=>QueueAutomaticEvaluation();
            spectrum.ConclusionSettingsChanged+=(s,e)=>QueueAutomaticEvaluation();
            spectrum.ViewChanged+=(s,e)=>{if(!spectrum.IsCameraViewNotification){QueueAutomaticEvaluation();PublishEvaluationMarkers();}};
            HandleCreated+=(s,e)=>{if(automaticEvaluationPending)automaticEvaluationTimer.Start();else QueueAutomaticEvaluation();};
            Disposed+=(s,e)=>{automaticEvaluationGeneration++;automaticEvaluationTimer.Stop();automaticEvaluationTimer.Dispose();};
        }
        void QueueAutomaticEvaluation()
        {
            if(automaticEvaluationTimer==null||IsDisposed||Disposing)return;
            string key=EvaluationContextKey();
            var geometry=pdmap.GetReviewConnectorGeometry();
            if(key==automaticEvaluationKey&&ReferenceEquals(automaticEvaluationSource,working)&&geometry.SameGeometry(automaticEvaluationGeometry))return;
            automaticEvaluationKey=key;automaticEvaluationSource=working;automaticEvaluationGeometry=geometry;
            automaticEvaluationGeneration++;automaticEvaluationPending=false;
            ValidateEvaluationContext();
            PublishEvaluationMarkers();
            // Keep stale-result invalidation, but do not enqueue conclusion work.
            // Saving captures the final report input without evaluating it.
            automaticEvaluationTimer.Stop();
        }
        void StartAutomaticEvaluation()
        {
            automaticEvaluationTimer.Stop();
            if(IsDisposed||Disposing||!IsHandleCreated||!automaticEvaluationPending)return;
            if(!SameAnalysisProjectPath(automaticEvaluationDataProject,projectPath)){automaticEvaluationPending=false;return;}
            if(evaluationBusy||IsAnalysisRunning||loadingData||suppressAnalysisDirty>0||pdmap.Capture||spectrum.Capture)
            {automaticEvaluationTimer.Start();return;}
            automaticEvaluationPending=false;
            ValidateEvaluationContext();
            // Merely loading an unchanged named analysis must not replace its
            // saved conclusion or turn the archive dirty.
            if(IsEvidenceV3(CurrentConclusionSnapshot())){PublishEvaluationMarkers();return;}
            var timer=Stopwatch.StartNew();EvaluationWork work=null;
            try
            {
                pdmap.CompleteReviewPreview();RefreshApproximationProjection();
                var geometry=pdmap.GetReviewConnectorGeometry();
                work=new EvaluationWork{Source=working,Geometry=geometry,Key=EvaluationContextKey(),Parameters=AnalysisParameterStamp(),
                    Generation=automaticEvaluationGeneration,Sequence=++EvaluationRequestSequence,
                    Voltage=spectrum.OperatingVoltageKv,VoltageSource="explicit OperatingVoltageKv",Years=spectrum.ConclusionCableServiceYears,
                    Body=spectrum.ConclusionCableBodyType,Joint=spectrum.ConclusionIntermediateJointType,Runtime=spectrum.Task15RuntimePath,English=english};
                work.Input=CreateEvaluationInput(out work.Rows,out work.Projected,out work.Accepted,out work.Invalid);
                LastEvaluationPreparationMilliseconds=timer.Elapsed.TotalMilliseconds;
                evaluationResult=null;evaluationDiagnostic="";evaluationSource=working;evaluationGeometry=geometry;evaluationContext=work.Key;
                if(work.Input.GetLength(0)==0)
                {
                    evaluationCode=work.Accepted==0?"NoAcceptedInput":"NoValidInput";
                    evaluationDiagnostic="Filtered accepted rows="+work.Accepted+"; excluded invalid rows="+work.Invalid;
                    PublishEvaluationMarkers();
                    NotifyReviewHost(EvaluationCompleted);RefreshEvaluationUi();return;
                }
                evaluationBusy=true;evaluationCode="Running";RefreshEvaluationUi();EvaluationAnalysisCount++;
                Task.Run(()=>ComputeAutomaticEvaluation(work)).ContinueWith(task=>
                {
                    // Observe errors even when the control has been closed.
                    Exception error=task.IsFaulted?task.Exception.GetBaseException():null;
                    Action finish=()=>FinishAutomaticEvaluation(work,task.IsCompleted&&!task.IsFaulted&&!task.IsCanceled?task.Result:null,error);
                    if(IsDisposed||Disposing)return;
                    if(!IsHandleCreated){evaluationBusy=false;automaticEvaluationPending=true;return;}
                    try{BeginInvoke(finish);}catch(InvalidOperationException){evaluationBusy=false;automaticEvaluationPending=true;}
                },TaskScheduler.Default);
            }
            catch(Exception ex)
            {
                if(IsFatalHostException(ex))throw;
                evaluationBusy=false;SetAutomaticEvaluationError(ex);NotifyReviewHost(EvaluationCompleted);RefreshEvaluationUi();
            }
        }
        ReviewEvaluationResult ComputeAutomaticEvaluation(EvaluationWork work)
        {
            var timer=Stopwatch.StartNew();
            try
            {
                var joints=EvaluationJoints(work.Input,work.Geometry);
                if(work.Voltage==0)work.VoltageSource="unknown U0; not inferred from test levels";
                if(!Finite(work.Voltage)||work.Voltage<0)throw new ArgumentException("Operating voltage is invalid.");
                var type=SpectrumTask15Bridge.ResolveType(SpectrumTask15Bridge.ResolvePath(work.Runtime));
                var result=SpectrumTask15Bridge.AnalyzeResult(type,work.Input,joints,work.Voltage,work.Years,work.Body,work.Joint,work.English);
                if(result.ValidPulses<=0)throw new InvalidOperationException("Task15 retained no valid pulses; no condition conclusion is available.");
                if(string.IsNullOrWhiteSpace(result.Summary))throw new InvalidOperationException("Task15 returned an empty conclusion summary.");
                SpectrumTask15Bridge.Parse(result.Locations,7);
                return new ReviewEvaluationResult(result,work.Rows,work.Projected,work.Accepted,work.Invalid,work.Voltage,work.VoltageSource,work.Sequence);
            }
            finally{LastEvaluationWorkerMilliseconds=timer.Elapsed.TotalMilliseconds;}
        }
        void FinishAutomaticEvaluation(EvaluationWork work,ReviewEvaluationResult result,Exception error)
        {
            if(IsDisposed||Disposing)return;
            evaluationBusy=false;
            if(work.Generation!=automaticEvaluationGeneration||!EvaluationContextMatches(work.Key,work.Source,work.Geometry)||work.Parameters!=AnalysisParameterStamp())
            {
                evaluationResult=null;evaluationContext=null;evaluationCode="Stale";
                QueueAutomaticEvaluation();automaticEvaluationPending=true;automaticEvaluationTimer.Start();return;
            }
            if(error!=null){SetAutomaticEvaluationError(error);PublishEvaluationMarkers();}
            else
            {
                evaluationResult=result;evaluationSource=work.Source;evaluationGeometry=work.Geometry;evaluationContext=work.Key;
                evaluationCode="Ready";evaluationDiagnostic="";
                if(EnableAiFeatures&&AiLearningMode)learningEvaluationSequence=result.RequestSequence;
                MarkAnalysisDirty();PublishEvaluationMarkers();
            }
            NotifyReviewHost(EvaluationCompleted);RefreshEvaluationUi();
            if(automaticEvaluationPending)automaticEvaluationTimer.Start();
        }
        void SetAutomaticEvaluationError(Exception ex)
        {
            evaluationResult=null;evaluationDiagnostic=ex.GetType().Name+": "+ex.Message;
            evaluationCode=ex is FileNotFoundException?"RuntimeUnavailable":ex is BadImageFormatException?"RuntimeArchitectureMismatch":ex is ArgumentException?"InvalidInput":"AnalysisUnavailable";
        }
        void PublishEvaluationMarkers()
        {
            ValidateEvaluationContext();
            spectrum.ShowConclusionAnnotations=false;
            spectrum.ClearProvidedConclusions();
        }
    }
}
