using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using HVX.SignalEvidence;
using HVX.UnifiedSpectrum;

namespace HVX.PdReview
{
    /// <summary>Immutable successful Task15 result. Arrays are defensive copies.
    /// Retained snapshots describe that request; use EvaluationResultIsCurrent before using one for current data.</summary>
    public sealed class ReviewEvaluationResult
    {
        readonly double[,] locations;
        readonly int[] sourceRows, projectedRows;
        readonly string[] statuses;
        readonly HVX.Location.AnalysisResult evidence;
        readonly double[][] joints;
        internal ReviewEvaluationResult(SpectrumConclusionResult result,int[] rows,int[] projected,int accepted,int invalid,
            double voltage,string voltageSource,int sequence)
        {
            SummaryText=result.Summary;Api=result.Api;ValidPulseCount=result.ValidPulses;
            locations=(double[,])result.Locations.Clone();statuses=(string[])result.Status.Clone();
            evidence=result.Evidence;
            joints=result.Joints==null?null:result.Joints.Select(j=>(double[])j.Clone()).ToArray();
            sourceRows=(int[])rows.Clone();projectedRows=(int[])projected.Clone();
            AcceptedRowCount=accepted;ExcludedInvalidRowCount=invalid;OperatingVoltageKv=voltage;
            OperatingVoltageSource=voltageSource;RequestSequence=sequence;CreatedUtc=DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture);
        }
        public bool IsValid { get { return true; } }
        public string SummaryText { get; private set; }
        public string Api { get; private set; }
        public string CreatedUtc { get; private set; }
        public int RequestSequence { get; private set; }
        public int InputRowCount { get { return sourceRows.Length; } }
        public int AcceptedRowCount { get; private set; }
        public int ExcludedInvalidRowCount { get; private set; }
        public int EndpointProjectedRowCount { get { return projectedRows.Length; } }
        public int ValidPulseCount { get; private set; }
        public double OperatingVoltageKv { get; private set; }
        public string OperatingVoltageSource { get; private set; }
        public string InputScope { get { return "All phases; review filter retained; column 35 == 1; valid Task17 rows"; } }
        public string InputAdapter { get { return "Task17 column22 amplitude ratio -> Task15 missing probability (NaN); column33 unchanged; column13 retains pre-accessory-snap coordinates; raw source/timing preserved; endpoint normalization within 2% retains original projection diagnostics; missing background is unknown"; } }
        public bool IsCalibratedProbability { get { return false; } }
        public double[][] JointArrays { get { return joints==null?null:joints.Select(j=>(double[])j.Clone()).ToArray(); } }
        public double[,] GetVisibleLocationMatrix(int mask) { return mask == 0 ? new double[0,11] : HVX.Location.EmbeddedConclusions.GetVisibleLocationMatrix(evidence,mask); }
        public int[] SourceRowIndices { get { return (int[])sourceRows.Clone(); } }
        public int[] EndpointProjectedSourceRowIndices { get { return (int[])projectedRows.Clone(); } }
        public double[,] LocationMatrix { get { return (double[,])locations.Clone(); } }
        public string[] StatusArray { get { return (string[])statuses.Clone(); } }
    }

    public sealed partial class PdReviewControl
    {
        ReviewEvaluationResult evaluationResult;
        bool evaluationBusy;
        string evaluationCode="NotRun",evaluationDiagnostic="",evaluationContext;
        double[,] evaluationSource;
        LabVIEWPdMap.PdMapControl.ReviewConnectorGeometry evaluationGeometry;
        Panel evaluationPopup;
        Label evaluationTitle;
        TextBox evaluationText;
        Button evaluationClose;

        [Browsable(false)] public bool EvaluationBusy { get { return evaluationBusy; } }
        [Browsable(false)] public int EvaluationRequestSequence { get; private set; }
        [Browsable(false)] public int EvaluationAnalysisCount { get; private set; }
        [Browsable(false)] public string EvaluationStatusCode { get { ValidateEvaluationContext();return evaluationCode; } }
        [Browsable(false)] public string EvaluationStatus { get { ValidateEvaluationContext();return EvaluationMessage(); } }
        [Browsable(false)] public string EvaluationDiagnostic { get { ValidateEvaluationContext();return evaluationDiagnostic; } }
        [Browsable(false)] public bool EvaluationResultIsCurrent { get { ValidateEvaluationContext();return evaluationResult!=null; } }
        [Browsable(false)] public ReviewEvaluationResult LatestEvaluationResult { get { ValidateEvaluationContext();return evaluationResult; } }
        public ReviewEvaluationResult GetLatestEvaluationResult() { return LatestEvaluationResult; }
        public event EventHandler EvaluationCompleted;

        string EvaluationContextKey()
        {
            // Selection, ABC display toggles and plot mode are deliberately absent.
            return ResultChangeSequence+"|"+FilterRefreshSequence+"|"+projectPath+"|"+pdmap.ProjectMetadataReadCount+"|"+english+"|"+
                spectrum.Task15RuntimePath+"|"+spectrum.OperatingVoltageKv.ToString("R",CultureInfo.InvariantCulture)+"|"+
                spectrum.ConclusionCableServiceYears.ToString("R",CultureInfo.InvariantCulture)+"|"+
                spectrum.ConclusionCableBodyType+"|"+spectrum.ConclusionIntermediateJointType+"|snap:"+pdmap.ApproximateCablePositions+"|"+pdmap.PositionTolerancePercent.ToString("R",CultureInfo.InvariantCulture)+"|joint:"+pdmap.JointApproximationErrorPercent.ToString("R",CultureInfo.InvariantCulture)+"|learning:"+AiLearningMode+"|parameters:"+AnalysisParameterStamp();
        }
        bool EvaluationContextMatches(string key,double[,] source,LabVIEWPdMap.PdMapControl.ReviewConnectorGeometry geometry)
        { return ReferenceEquals(source,working)&&key==EvaluationContextKey()&&pdmap.GetReviewConnectorGeometry().SameGeometry(geometry); }
        void ValidateEvaluationContext()
        {
            if(evaluationContext==null||EvaluationContextMatches(evaluationContext,evaluationSource,evaluationGeometry))return;
            evaluationResult=null;evaluationContext=null;evaluationSource=null;evaluationGeometry=null;
            evaluationCode="Stale";evaluationDiagnostic="";
            spectrum.ClearProvidedConclusions();
        }

        /// <summary>Evaluate all filtered accepted source rows, independent of ABC display and selection.
        /// Returns a current successful result, including a cached same-input result. Does not write files or change acceptance.</summary>
        public bool RequestEvaluation()
        {
            if (ReportStageConclusionsOnly)
            {
                evaluationCode="ReportOnly";
                evaluationDiagnostic="Conclusions are generated from the saved report input, not during review.";
                return false;
            }
            if(evaluationBusy)return false;
            evaluationBusy=true;
            try
            {
                // Flush a final drag before freezing the matrix and filter identity.
                pdmap.CompleteReviewPreview();RefreshApproximationProjection();ValidateEvaluationContext();
                if(evaluationResult!=null){if(AiLearningMode)learningEvaluationSequence=evaluationResult.RequestSequence;return true;}
                EvaluationRequestSequence++;evaluationResult=null;evaluationDiagnostic="";
                evaluationSource=working;evaluationGeometry=pdmap.GetReviewConnectorGeometry();evaluationContext=EvaluationContextKey();
                evaluationCode="Running";RefreshEvaluationUi();
                int[] rows,projected;int accepted,invalid;
                var input=CreateEvaluationInput(out rows,out projected,out accepted,out invalid);
                if(input.GetLength(0)==0)
                {
                    evaluationCode=accepted==0?"NoAcceptedInput":"NoValidInput";
                    evaluationDiagnostic="Filtered accepted rows="+accepted+"; excluded invalid rows="+invalid;
                    NotifyReviewHost(EvaluationCompleted);return false;
                }
                double[][] joints=EvaluationJoints(input,evaluationGeometry);
                string voltageSource="explicit OperatingVoltageKv";
                double voltage=spectrum.OperatingVoltageKv;
                if(voltage==0)voltageSource="unknown U0; not inferred from test levels";
                if(!Finite(voltage)||voltage<0)throw new ArgumentException("Operating voltage is invalid.");
                Type type=SpectrumTask15Bridge.ResolveType(SpectrumTask15Bridge.ResolvePath(spectrum.Task15RuntimePath));
                EvaluationAnalysisCount++;
                var result=SpectrumTask15Bridge.AnalyzeResult(type,input,joints,voltage,spectrum.ConclusionCableServiceYears,
                    spectrum.ConclusionCableBodyType,spectrum.ConclusionIntermediateJointType,english);
                // A successful dependency call with zero usable pulses is NOT a no-discharge finding.
                if(result.ValidPulses<=0)throw new InvalidOperationException("Task15 retained no valid pulses; no condition conclusion is available.");
                if(string.IsNullOrWhiteSpace(result.Summary))throw new InvalidOperationException("Task15 returned an empty conclusion summary.");
                SpectrumTask15Bridge.Parse(result.Locations,7); // Validate every phase, never the visible subset.
                if(!EvaluationContextMatches(evaluationContext,evaluationSource,evaluationGeometry))
                { evaluationCode="Stale";evaluationContext=null;NotifyReviewHost(EvaluationCompleted);return false; }
                evaluationResult=new ReviewEvaluationResult(result,rows,projected,accepted,invalid,voltage,voltageSource,EvaluationRequestSequence);
                if(AiLearningMode)learningEvaluationSequence=evaluationResult.RequestSequence;
                evaluationCode="Ready";MarkAnalysisDirty();NotifyReviewHost(EvaluationCompleted);
                ValidateEvaluationContext();return evaluationResult!=null;
            }
            catch(Exception ex)
            {
                if(ex is OutOfMemoryException||ex is StackOverflowException||ex is AccessViolationException||ex is System.Threading.ThreadAbortException)throw;
                evaluationResult=null;evaluationDiagnostic=ex.GetType().Name+": "+ex.Message;
                evaluationCode=ex is FileNotFoundException?"RuntimeUnavailable":ex is BadImageFormatException?"RuntimeArchitectureMismatch":
                    ex is ArgumentException?"InvalidInput":"AnalysisUnavailable";
                NotifyReviewHost(EvaluationCompleted);return false;
            }
            finally { evaluationBusy=false;RefreshEvaluationUi(); }
        }

        double[,] CreateEvaluationInput(out int[] indices,out int[] projected,out int accepted,out int invalid)
        {
            var rows=new List<int>();var endpoints=new List<int>();accepted=invalid=0;
            for(int r=0;r<SourceRowCount;r++)
            {
                if(reviewMask==null||reviewMask.Length!=SourceRowCount)throw new ArgumentException("Review filter is not ready.");
                if(!reviewMask[r]||working[r,35]!=1||IsAcquisitionReferenceOnly(r))continue;
                accepted++;
                double phase=working[r,0],length=working[r,5],position=working[r,13];
                bool valid=Finite(phase)&&phase>=1&&phase<=3&&phase==Math.Truncate(phase)&&
                    Finite(working[r,1])&&Finite(working[r,2])&&working[r,2]>.05&&Finite(working[r,3])&&
                    Finite(working[r,15])&&working[r,15]>0&&Finite(working[r,23])&&working[r,23]>=0&&
                    Finite(working[r,24])&&working[r,24]>=0&&EvaluationPairIsValid(working,r)&&
                    BoundaryConfidenceMath.WithinTolerance(position,length);
                if(!valid){invalid++;continue;}
                double diagnostic=position;
                if(original.GetLength(0)==SourceRowCount&&original.GetLength(1)>13&&original[r,5]==length&&
                    original[r,10]==working[r,10]&&original[r,11]==working[r,11]&&Finite(original[r,13])&&
                    BoundaryConfidenceMath.ProjectEndpointPosition(original[r,13],length)==position)diagnostic=original[r,13];
                rows.Add(r);if(diagnostic<0||diagnostic>length)endpoints.Add(r);
            }
            var matrix=new double[rows.Count,working.GetLength(1)];
            for(int i=0;i<rows.Count;i++)
            {
                int r=rows[i];for(int c=0;c<working.GetLength(1);c++)matrix[i,c]=working[r,c];
                matrix[i,13]=working[r,13]; // Chart accessory snapping is presentation, not evidence.
                // Current Task17 col22 is amplitude ratio, not Task15's legacy probability.
                // Neither that ratio nor confidence col33 is a substitute for independent evidence.
                matrix[i,22]=double.NaN;
            }
            indices=rows.ToArray();projected=endpoints.ToArray();return matrix;
        }
        static bool EvaluationPairIsValid(double[,] matrix,int row)
        {
            // A stale finite coordinate is not evidence of a real located pulse pair.
            // Do not reconstruct missing timing from cached coordinates or alter source rows.
            double sampleRate=matrix[row,7],incident=matrix[row,10],reflection=matrix[row,11],delay=matrix[row,12];
            if(!Finite(sampleRate)||sampleRate<=0)return false;
            if(!Finite(incident)||incident<0||incident>int.MaxValue||incident!=Math.Truncate(incident))return false;
            if(!Finite(reflection)||reflection<0||reflection>int.MaxValue||reflection!=Math.Truncate(reflection))return false;
            if(!Finite(delay)||delay<0)return false;
            // Explicit coincident picks are valid evidence; missing or contradictory delay is not.
            return reflection-incident==delay;
        }
        static double[][] EvaluationJoints(double[,] input,LabVIEWPdMap.PdMapControl.ReviewConnectorGeometry geometry)
        {
            var result=new double[3][];
            double fallback=input[0,5];
            for(int phase=1;phase<=3;phase++)
            {
                double length=double.NaN;
                for(int r=0;r<input.GetLength(0);r++)if(input[r,0]==phase)
                {
                    double value=input[r,5];
                    if(Finite(length)&&Math.Abs(length-value)>Math.Max(1e-6,length*1e-9))
                        throw new ArgumentException("Accepted records have inconsistent cable lengths for phase "+phase+".");
                    length=value;
                }
                // Unobserved phases do not borrow another phase's joints.
                if(!Finite(length)){result[phase-1]=new double[0];continue;}
                result[phase-1]=new[]{0.0,length}.Concat(geometry.JointPositionsForPhase(phase,length)).Distinct().OrderBy(v=>v).ToArray();
            }
            return result;
        }
        string EvaluationMessage()
        {
            switch(evaluationCode)
            {
                case "ReportOnly":return L("Generate conclusions in Report after saving the final analysis.","保存最终分析后，请在报告中生成结论。");
                case "Ready":return L("Evaluation completed.","评估完成。");
                case "Running":return L("Evaluating…","正在评估…");
                case "Stale":return L("Data or settings changed. Waiting for the latest evaluation.","数据或设置已变更，正在等待最新评估。");
                case "NoAcceptedInput":return L("No accepted data remains after filtering. No evaluation is available.","过滤后没有已接受的数据，尚不能评估。");
                case "NoValidInput":return L("Accepted data has no valid evaluation input. Check the results.","已接受数据不满足评估条件，请核对结果。");
                case "RuntimeUnavailable":return L("Evaluation component is unavailable. Check installation.","评估组件不可用，请检查安装。");
                case "RuntimeArchitectureMismatch":return L("Evaluation component does not match the host architecture.","评估组件与当前程序位数不匹配。");
                case "InvalidInput":return L("Evaluation data or cable settings are inconsistent. Please check.","评估数据或电缆设置不一致，请检查。");
                case "AnalysisUnavailable":return L("Evaluation could not be completed. This is not a no-discharge result.","暂时无法完成评估，不能据此认定无放电。");
                default:return L("Evaluate all filtered accepted data.","评估过滤后已接受的全部数据。");
            }
        }
        void RefreshEvaluationUi()
        {
            ValidateEvaluationContext();
            // This button now opens saved analyses, not the evaluation popup.
            // A second, evaluation-only gate briefly enabled it on every LOC
            // progress update before RefreshReviewUi disabled it again.
            RefreshAnalysisArchiveActions();
            RefreshAiLearningUi();
            if(evaluationPopup==null||!evaluationPopup.Visible)return;
            evaluationTitle.Text=L("Evaluation result","评估结果");evaluationClose.Text=L("Close","关闭");
            string text=EvaluationMessage();
            if(evaluationResult!=null)
            {
                var result=evaluationResult;
                text+=(L("\r\nScope: all filtered accepted phases; input rows: ","\r\n范围：过滤后已接受的全部相别；输入条数："))+result.InputRowCount+
                    (L("; invalid rows excluded: ","；无效记录排除："))+result.ExcludedInvalidRowCount+
                    (L("; endpoint approximations within 2%: ","；2%范围内端点近似："))+result.EndpointProjectedRowCount+"\r\n";
                text+=(L("Operating voltage U0: ","运行相电压 U0："))+result.OperatingVoltageKv.ToString("0.###",CultureInfo.InvariantCulture)+" kV"+
                    (result.OperatingVoltageKv>0?(L(" (specified)","（指定值）")):(L(" (unknown; not inferred from test levels)","（未知；未从试验电压推断）")))+"\r\n\r\n"+result.SummaryText;
                if(EnableAiFeatures&&!AiLearningMode)
                {
                    var suggestion=GetLocalAiConclusionSuggestion();
                    if(suggestion.Available)text+="\r\n\r\n"+L("Local AI suggestion (manual review required): ","本地轻量 AI 建议（须人工复核）：")+suggestion.SuggestedConclusion+
                        "\r\n"+L("Engineering similarity: ","工程相似度：")+(suggestion.Confidence*100).ToString("0.0",CultureInfo.InvariantCulture)+"%";
                }
            }
            if(evaluationText.Text!=text)evaluationText.Text=text;
            LayoutEvaluationPopup();
        }
        void InitializeEvaluationPopup()
        {
            evaluationPopup=new Panel { Name="ReviewEvaluationPopup",Visible=false,BackColor=Color.White,Padding=new Padding(10) };
            evaluationPopup.Paint+=(s,e)=>{using(var border=new Pen(ReviewTheme.Border))e.Graphics.DrawRectangle(border,0,0,Math.Max(0,evaluationPopup.Width-1),Math.Max(0,evaluationPopup.Height-1));};
            evaluationTitle=new Label { Dock=DockStyle.Top,Height=26,TextAlign=ContentAlignment.MiddleLeft,ForeColor=ReviewTheme.Ink };
            evaluationText=new TextBox { Name="ReviewEvaluationText",Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BorderStyle=BorderStyle.None,BackColor=Color.White,ForeColor=ReviewTheme.Ink };
            var footer=new Panel { Dock=DockStyle.Bottom,Height=35 };
            evaluationClose=ActionButton(76);evaluationClose.Name="ReviewEvaluationClose";evaluationClose.Dock=DockStyle.Right;evaluationClose.Click+=(s,e)=>evaluationPopup.Hide();footer.Controls.Add(evaluationClose);
            InitializeConclusionFeedbackButton(footer);
            evaluationPopup.Controls.Add(evaluationText);evaluationPopup.Controls.Add(evaluationTitle);evaluationPopup.Controls.Add(footer);Controls.Add(evaluationPopup);
            Resize+=(s,e)=>LayoutEvaluationPopup();
        }
        void LayoutEvaluationPopup()
        {
            if(evaluationPopup==null)return;
            // A short unavailable/stale message should not obscure the waveform.
            // Successful conclusions keep their full reading area; larger fonts
            // gain space without ever forcing the LabVIEW host control to grow.
            double scale=Math.Max(1,Font.SizeInPoints/9.5);
            int preferredWidth=(int)Math.Ceiling((evaluationResult==null?560:860)*scale);
            int preferredHeight=(int)Math.Ceiling((evaluationResult==null?220:600)*scale);
            int width=Math.Min(preferredWidth,Math.Max(100,ClientSize.Width-24)),height=Math.Min(preferredHeight,Math.Max(100,ClientSize.Height-24));
            evaluationTitle.Height=Math.Max(26,Font.Height+6);
            evaluationClose.Parent.Height=Math.Max(35,Font.Height+14);
            evaluationClose.Width=Math.Max(76,TextRenderer.MeasureText(evaluationClose.Text,evaluationClose.Font).Width+20);
            var bounds=new Rectangle((ClientSize.Width-width)/2,(ClientSize.Height-height)/2,width,height);
            if(evaluationPopup.Bounds!=bounds)evaluationPopup.Bounds=bounds;
        }
        /// <summary>Show the current evaluation or a concise unavailable/stale message inside this control. Does not rerun Task15.</summary>
        public void ShowEvaluationResult()
        {if(ReportStageConclusionsOnly)return;LayoutEvaluationPopup();evaluationPopup.Show();evaluationPopup.BringToFront();RefreshEvaluationUi();evaluationClose.Focus();}
        [Browsable(false)] public bool ReportStageConclusionsOnly {get{return true;}}
    }
}
