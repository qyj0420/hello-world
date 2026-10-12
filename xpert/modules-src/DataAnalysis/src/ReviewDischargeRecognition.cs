using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace HVX.PdReview
{
    public sealed partial class PdReviewControl
    {
        string modelLibraryPath="",learningStatusMessage="";
        ReviewTask12Runtime task12Runtime;
        DischargeRecognitionResult dischargeResult;
        bool recognitionBusy,recognitionCleanupRegistered;
        int recognitionGeneration;
        string learningRootStamp,learningRootCache;
        static readonly string[,] dischargeLabels={
            {"tip","尖端放电","Tip discharge"},{"internal","气隙/内部放电","Internal discharge"},
            {"surface","沿面放电","Surface discharge"},{"floating","悬浮放电","Floating discharge"},
            {"wrong_tape","接头错缠绝缘胶带","Wrong insulation tape"},{"missing_stress_tube","未安装应力管","Missing stress tube"},
            {"noise","背景噪声","Background noise"},{"invalid","无效数据","Invalid data"}};
        sealed class LearningLabelSelection
        {
            internal string CanonicalLabelKeys,ManualDischargeType,TaxonomyStatus;
            internal bool Task12Compatible;
        }

        [Category("Data"),DefaultValue(""),Description("Task12 encrypted .pdm file or a directory containing one PDM. Empty uses the deployed Task12 directory.")]
        public string ModelLibraryPath
        { get {return modelLibraryPath;} set {string path=(value??"").Trim();if(path==modelLibraryPath)return;modelLibraryPath=path;recognitionGeneration++;dischargeResult=null;learningRootStamp=null;QueueDischargeIdentification();} }
        [Browsable(false)] public bool DischargeRecognitionBusy {get{return recognitionBusy;}}
        [Browsable(false)] public string LearningStatusMessage {get{return learningStatusMessage;}}
        [Browsable(false)] public string[,] AvailableDischargeLabels {get{return (string[,])dischargeLabels.Clone();}}
        [Browsable(false)] public string LearningSamplesDirectory {get{return EnableAiFeatures?RecognitionLearningRoot():"";}}
        [Browsable(false)] public int LearningSampleCount {get{return EnableAiFeatures?ReviewLearningTransactions.Count(RecognitionLearningRoot()):0;}}
        [Browsable(false)] public DischargeRecognitionResult CurrentDischargeRecognition
        {get{if(!DefectRecognitionEnabled)return null;if(dischargeResult!=null&&dischargeResult.ContextKey!=RecognitionContext())dischargeResult=null;return dischargeResult;}}
        public event EventHandler DischargeRecognitionCompleted;

        string RecognitionContext()
        {
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(working)+"|"+ResultChangeSequence+"|"+FilterRefreshSequence+"|"+spectrum.DisplayModeIndex+"|"+spectrum.UseCompensatedCharge+"|"+
                projectPath+"|"+modelLibraryPath+"|"+RecognitionModelStamp()+"|"+english+"|"+String.Join(",",spectrum.VisibleSourceRowIndices);
        }
        string RecognitionModelStamp()
        {try{var file=new FileInfo(ReviewTask12Runtime.ResolveModelPath(modelLibraryPath));return file.FullName+"|"+file.Length+"|"+file.LastWriteTimeUtc.Ticks;}catch{return "unavailable";}}
        RecognitionSnapshot CaptureRecognitionInput()
        {
            if(InvokeRequired)throw new InvalidOperationException("Capture PRPD recognition input on the control's UI thread.");
            if(spectrum.DisplayModeIndex!=11&&spectrum.DisplayModeIndex!=12)throw new InvalidOperationException(L("Select PRPD or L-PRPD before recognition.","请先切换到 PRPD 或 L-PRPD 谱图。"));
            if(pdmap.Capture||spectrum.Capture)throw new InvalidOperationException(L("Finish the current plot gesture before recognition.","请先结束当前波形或谱图拖动操作。"));
            var rows=spectrum.VisibleSourceRowIndices;
            var valid=new List<int>();var angles=new List<double>();var charges=new List<double>();var phases=new List<int>();
            foreach(int row in rows)
            {
                if(row<0||row>=working.GetLength(0)||IsAcquisitionReferenceOnly(row))continue;
                double phi=working[row,3],q=spectrum.UseCompensatedCharge&&Finite(working[row,16])?working[row,16]:working[row,15],p=working[row,0];
                if(!Finite(phi)||!Finite(q)||!Finite(p)||p<0||p>3||p!=Math.Truncate(p))continue;
                valid.Add(row);angles.Add(phi);charges.Add(q);phases.Add((int)p);
            }
            return new RecognitionSnapshot {Rows=valid.ToArray(),Angles=angles.ToArray(),Charges=charges.ToArray(),Phases=phases.ToArray(),
                Context=RecognitionContext(),English=english,Project=projectPath,ModelPath=modelLibraryPath,
                Scope=(english?"Current visible analyzed MAP pulses; not complete raw DEM; ":"当前谱图可见的已分析 MAP 脉冲，不是完整原始 DEM；")+
                    (spectrum.UseCompensatedCharge?(english?"compensated charge":"补偿放电量"):(english?"raw charge":"原始放电量"))+
                    "; source column phase=3, Q="+(spectrum.UseCompensatedCharge?"16 (finite), else 15":"15")};
        }
        ReviewTask12Runtime RecognitionRuntime()
        {
            if(task12Runtime==null)task12Runtime=new ReviewTask12Runtime();
            if(!recognitionCleanupRegistered)
            {
                recognitionCleanupRegistered=true;
                Disposed+=(s,e)=>{
                    recognitionGeneration++;var runtime=task12Runtime;task12Runtime=null;
                    // Idle sessions must be released before host shutdown. Only an active
                    // inference uses deferred cleanup, so closing a window cannot block on it.
                    if(runtime!=null){if(recognitionBusy)Task.Run(()=>runtime.Dispose());else runtime.Dispose();}
                };
            }
            return task12Runtime;
        }
        static string RecognitionValue(Dictionary<string,object> result,string key)
        {object value;return result!=null&&result.TryGetValue(key,out value)?Convert.ToString(value,CultureInfo.InvariantCulture):"";}
        static bool RecognitionFlag(Dictionary<string,object> result,string key)
        {object value;return result!=null&&result.TryGetValue(key,out value)&&value is bool&&(bool)value;}
        static string RecognitionKeys(string label)
        {
            if(label=="噪声/干扰信号")return "noise";
            var keys=new List<string>();foreach(string part in (label??"").Split('+'))
                for(int i=0;i<dischargeLabels.GetLength(0);i++)if(part.Trim()==dischargeLabels[i,0]||part.Trim()==dischargeLabels[i,1])keys.Add(dischargeLabels[i,0]);
            return String.Join("+",keys.Distinct());
        }
        static string RecognitionEnglish(string label,string keys)
        {
            var labels=new List<string>();foreach(string key in keys.Split('+'))for(int i=0;i<dischargeLabels.GetLength(0);i++)if(key==dischargeLabels[i,0])labels.Add(dischargeLabels[i,2]);
            return labels.Count>0?String.Join(" + ",labels):"Unclassified";
        }
        DischargeRecognitionResult RunRecognition(RecognitionSnapshot input,ReviewTask12Runtime runtime)
        { return RunRecognitionCore(input,runtime,()=>DefectRecognitionEnabled); }
        internal static DischargeRecognitionResult RunRecognitionCore(RecognitionSnapshot input,ReviewTask12Runtime runtime,Func<bool> enabled)
        {
            string model="",sha="";
            try
            {
                if(!enabled())return RecognitionFailure(input,"Disabled",input.English?"AI disabled or input changed.":"AI 已关闭或输入已改变。",sha);
                if(input.Phases.GroupBy(p=>p).All(group=>group.Count()<3))
                    return RecognitionFailure(input,"InsufficientInput",input.English?"Each visible phase has fewer than three PRPD samples; no classification was attempted.":"各可见相别的 PRPD 样本均少于三个，未执行识别。",sha);
                model=ReviewTask12Runtime.ResolveModelPath(input.ModelPath);sha=ReviewTask12Runtime.HashFile(model);
                var labels=new List<string>();var diagnostics=new List<string>();var scores=new List<DischargeClassScore>();string soleKeys="";bool any=false,all=true;
                var groups=Enumerable.Range(0,input.Rows.Length).GroupBy(i=>input.Phases[i]).ToArray();
                if(groups.Length==0)return RecognitionFailure(input,"InsufficientInput",input.English?"No visible PRPD samples.":"当前 PRPD 没有可见样本。",sha);
                foreach(var group in groups)
                {
                    if(!enabled())return RecognitionFailure(input,"Disabled",input.English?"AI disabled or input changed.":"AI 已关闭或输入已改变。",sha);
                    string phase=group.Key==0?"ALL":((char)('A'+group.Key-1)).ToString();int[] indices=group.ToArray();
                    if(indices.Length<3){labels.Add(phase+": "+(input.English?"Insufficient samples":"样本不足"));all=false;continue;}
                    var data=runtime.Classify(model,indices.Select(i=>input.Angles[i]).ToArray(),indices.Select(i=>input.Charges[i]).ToArray());
                    diagnostics.Add(phase+": "+new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(data));
                    scores.AddRange(ReadRecognitionScores(data,phase,input.English));
                    bool ok=RecognitionFlag(data,"ok")&&!RecognitionFlag(data,"invalid");
                    string label=RecognitionValue(data,"result"),keys=RecognitionKeys(label);
                    // Result is the thresholded model output. Never promote argmax/BestType into a recognized defect.
                    if(!ok||keys.Length==0){all=false;labels.Add(phase+": "+(input.English?"Unclassified / insufficient evidence":"未识别 / 证据不足"));continue;}
                    any=true;soleKeys=keys;labels.Add(phase+": "+(input.English?RecognitionEnglish(label,keys):label));
                }
                if(ReviewTask12Runtime.HashFile(model)!=sha) return RecognitionFailure(input,"StaleModel",input.English?"The model changed during recognition; retry.":"识别期间模型已改变，请重新识别。",sha);
                return new DischargeRecognitionResult(any,String.Join("; ",labels),all?"Ready":any?"Partial":"Unclassified",
                    input.English?"Phase-separated model indication from visible MAP pulses; not a calibrated failure probability or a no-defect conclusion.":"按物理相别分别识别当前可见 MAP 脉冲；模型提示不是失效概率，也不是无缺陷结论。",
                    groups.Length==1?soleKeys:"",sha,input.Scope,input.Rows.Length,String.Join("\r\n",diagnostics),input.Context,scores.ToArray());
            }
            catch(Exception ex)
            {
                if(ex is OutOfMemoryException||ex is AccessViolationException||ex is System.Threading.ThreadAbortException)throw;
                return RecognitionFailure(input,"Unavailable",(input.English?"Recognition unavailable: ":"识别不可用：")+ex.Message,sha);
            }
        }
        static DischargeRecognitionResult RecognitionFailure(RecognitionSnapshot input,string code,string message,string sha)
        {return new DischargeRecognitionResult(false,"",code,message,"",sha,input.Scope,input.Rows.Length,"",input.Context);}
        public DischargeRecognitionResult RecognizeCurrentDischarge()
        {
            RequireDefectRecognition();
            if(AiLearningMode)throw new InvalidOperationException(L("Automatic discharge recognition is paused in AI learning mode; add a manually verified type.","AI 学习模式已暂停自动放电类型识别，请添加人工确认类型。"));
            if(recognitionBusy)throw new InvalidOperationException("Recognition is already running.");
            var input=CaptureRecognitionInput();recognitionBusy=true;
            try{var result=RunRecognition(input,RecognitionRuntime());if(input.Context==RecognitionContext())dischargeResult=result;return result;}
            finally{recognitionBusy=false;NotifyReviewHost(DischargeRecognitionCompleted);}
        }
        public Task<DischargeRecognitionResult> RecognizeCurrentDischargeAsync()
        {
            RequireDefectRecognition();
            if(AiLearningMode)throw new InvalidOperationException(L("Automatic discharge recognition is paused in AI learning mode; add a manually verified type.","AI 学习模式已暂停自动放电类型识别，请添加人工确认类型。"));
            if(recognitionBusy)throw new InvalidOperationException("Recognition is already running.");
            var input=CaptureRecognitionInput();var runtime=RecognitionRuntime();int generation=++recognitionGeneration;
            var completion=new TaskCompletionSource<DischargeRecognitionResult>();recognitionBusy=true;
            // Ensure a dispatcher exists before leaving the UI thread (LabVIEW may not install a SynchronizationContext).
            var handle=Handle;
            Task.Run(()=>RunRecognition(input,runtime)).ContinueWith(task=>
            {
                Action finish=()=>{
                    recognitionBusy=false;
                    if(task.IsFaulted){completion.TrySetException(task.Exception.InnerExceptions);return;}
                    var result=task.Result;
                    if(DefectRecognitionEnabled&&generation==recognitionGeneration&&input.Context==RecognitionContext())dischargeResult=result;
                    else result=RecognitionFailure(input,"Stale",input.English?"Data, view or model selection changed; the old result was discarded.":"数据、谱图或模型选择已变化，旧结果已丢弃。",result.ModelSha256);
                    NotifyReviewHost(DischargeRecognitionCompleted);completion.TrySetResult(result);
                };
                if(IsDisposed||Disposing){completion.TrySetCanceled();return;}
                try{BeginInvoke(finish);}catch(InvalidOperationException){completion.TrySetCanceled();}
            },TaskScheduler.Default);
            return completion.Task;
        }
        string RecognitionLearningRoot()
        {
            string stamp=RecognitionModelStamp();if(learningRootStamp==stamp&&learningRootCache!=null)return learningRootCache;
            string path=ReviewTask12Runtime.ResolveModelPath(modelLibraryPath);
            learningRootCache=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HVX","PdReview","Task12Learning",ReviewTask12Runtime.HashFile(path));
            learningRootStamp=stamp;return learningRootCache;
        }
        public bool AddConfirmedLearningSample(string labelKeys,string operatorName)
        {
            return AddConfirmedLearningSample(labelKeys,operatorName,"");
        }
        public bool AddConfirmedLearningSample(string labelKeys,string operatorName,string operator_label_text)
        {
            try
            {
                RequireAiFeatures();
                var input=CaptureRecognitionInput();
                if(input.Rows.Length<3)throw new ArgumentException(L("At least three visible samples are required; this is not a reliability guarantee.","至少需要三个可见样本；此下限不代表识别可靠性保证。"));
                if(input.Phases.Distinct().Count()!=1)throw new ArgumentException(L("Show only one physical phase before confirming a learning sample.","请先仅显示一个物理相别，再确认学习样本。"));
                var selection=ResolveLearningLabelSelection(labelKeys,operator_label_text);
                string model=ReviewTask12Runtime.ResolveModelPath(modelLibraryPath),sha=ReviewTask12Runtime.HashFile(model);
                ReviewLearningTransactions.Add(RecognitionLearningRoot(),input,model,sha,selection.CanonicalLabelKeys,operatorName,
                    selection.ManualDischargeType,selection.Task12Compatible,selection.TaxonomyStatus,selection.Task12Compatible?RecognitionRuntime():null);
                learningStatusMessage=selection.Task12Compatible?
                    L("Confirmed sample added to the pending training library. The production model was not changed.","已加入人工确认待训练样本库；生产模型未改变。"):
                    L("Custom type saved for review. The current recognition model was not changed.","自定义类型已保存并等待审核；当前识别模型未改变。");
                return true;
            }
            catch(Exception ex){learningStatusMessage=ex.Message;return false;}
        }
        static LearningLabelSelection ResolveLearningLabelSelection(string labelKeys,string operatorLabelText)
        {
            string original=operatorLabelText??"";
            if(original.Length>ReviewLearningTransactions.ManualDischargeTypeMaxLength)throw new ArgumentException("The manually entered discharge type is too long.");
            if(original.Any(Char.IsControl))throw new ArgumentException("The manually entered discharge type cannot contain control characters.");
            string manual=original.Trim();
            var keys=new List<string>();
            if(!String.IsNullOrWhiteSpace(labelKeys))keys.AddRange(ValidateLearningLabels(labelKeys).Split('+'));
            bool unknown=false;
            if(manual.Length>0)
            {
                string exact;
                if(TryMapLearningLabel(manual,out exact))keys.Add(exact);
                else
                {
                    string[] tokens=manual.Split(new[]{'+',',',';','，','；'},StringSplitOptions.RemoveEmptyEntries);
                    if(tokens.Length==0)unknown=true;
                    foreach(string token in tokens)
                    {
                        string mapped;
                        if(TryMapLearningLabel(token.Trim(),out mapped))keys.Add(mapped);else unknown=true;
                    }
                }
            }
            keys=Enumerable.Range(0,dischargeLabels.GetLength(0)).Select(i=>dischargeLabels[i,0]).Where(key=>keys.Contains(key)).ToList();
            if(keys.Any(k=>k=="noise"||k=="invalid")&&keys.Count>1)throw new ArgumentException("Noise or invalid data cannot be combined with another label.");
            if(keys.Count==0&&manual.Length==0)throw new ArgumentException("Select a supported type or enter a manually confirmed discharge type.");
            return new LearningLabelSelection {CanonicalLabelKeys=String.Join("+",keys),ManualDischargeType=manual,
                Task12Compatible=!unknown&&keys.Count>0,TaxonomyStatus=unknown?"pending_taxonomy_review":manual.Length>0?"canonical_mapped":"canonical_confirmed"};
        }
        static bool TryMapLearningLabel(string text,out string key)
        {
            text=(text??"").Trim();
            if(String.Equals(text,"噪声/干扰信号",StringComparison.OrdinalIgnoreCase)){key="noise";return true;}
            for(int i=0;i<dischargeLabels.GetLength(0);i++)
                if(String.Equals(text,dischargeLabels[i,0],StringComparison.OrdinalIgnoreCase)||
                    String.Equals(text,dischargeLabels[i,1],StringComparison.OrdinalIgnoreCase)||
                    String.Equals(text,dischargeLabels[i,2],StringComparison.OrdinalIgnoreCase))
                {key=dischargeLabels[i,0];return true;}
            key="";return false;
        }
        static string ValidateLearningLabels(string labels)
        {
            string[] keys=(labels??"").Split(new[]{'+',',',';','，','；'},StringSplitOptions.RemoveEmptyEntries).Select(s=>s.Trim().ToLowerInvariant()).Distinct().ToArray();
            if(keys.Length==0||keys.Any(key=>!Enumerable.Range(0,dischargeLabels.GetLength(0)).Any(i=>dischargeLabels[i,0]==key)))throw new ArgumentException("Select at least one supported manually confirmed label.");
            if(keys.Any(k=>k=="noise"||k=="invalid")&&keys.Length>1)throw new ArgumentException("Noise or invalid data cannot be combined with another label.");
            return String.Join("+",keys);
        }
        public bool UndoLatestLearningSample()
        {
            if(!EnableAiFeatures){learningStatusMessage=AiFeaturesDisabledMessage();return false;}
            try{bool changed=ReviewLearningTransactions.Undo(RecognitionLearningRoot());learningStatusMessage=changed?L("The latest control-owned sample was moved to history; the model is unchanged.","最近一次本控件学习样本已移入历史备份；模型未改变。"):L("No control-owned sample can be undone.","没有可撤销的本控件学习样本。");return changed;}
            catch(Exception ex){learningStatusMessage=ex.Message;return false;}
        }
        public bool ResetLearningSamples()
        {
            if(!EnableAiFeatures){learningStatusMessage=AiFeaturesDisabledMessage();return false;}
            try{int count=ReviewLearningTransactions.Reset(RecognitionLearningRoot());learningStatusMessage=L("Control-owned pending samples archived; history preserved and base model unchanged: ","已清空本控件待训练样本并保留历史备份；基础模型未改变，归档数量：")+count;return true;}
            catch(Exception ex){learningStatusMessage=ex.Message;return false;}
        }
    }
}
