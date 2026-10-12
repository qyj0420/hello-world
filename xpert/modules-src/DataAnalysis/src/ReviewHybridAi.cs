using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using HVX.UnifiedSpectrum;

namespace HVX.PdReview
{
    /// <summary>A local, operator-reviewable suggestion. It never accepts a MAP automatically.</summary>
    public sealed class LocalAiAnalysisSuggestion
    {
        internal LocalAiAnalysisSuggestion(bool available,int samples,double confidence,double threshold,int reflection,string status)
        { Available=available;SampleCount=samples;Confidence=confidence;SuggestedThresholdPc=threshold;SuggestedReflectionIndex=reflection;Status=status??""; }
        public bool Available { get; private set; }
        public int SampleCount { get; private set; }
        public double Confidence { get; private set; }
        public double SuggestedThresholdPc { get; private set; }
        public int SuggestedReflectionIndex { get; private set; }
        public string Status { get; private set; }
    }

    /// <summary>A local retrieval suggestion learned from rated human corrections.</summary>
    public sealed class LocalAiConclusionSuggestion
    {
        internal LocalAiConclusionSuggestion(bool available,int samples,double confidence,string text,string status)
        { Available=available;SampleCount=samples;Confidence=confidence;SuggestedConclusion=text??"";Status=status??""; }
        public bool Available { get; private set; }
        public int SampleCount { get; private set; }
        public double Confidence { get; private set; }
        public string SuggestedConclusion { get; private set; }
        public string Status { get; private set; }
    }

    /// <summary>
    /// Append-only local learning store. A transaction is data for later local training,
    /// never an executable model and never a silent modification of the production model.
    /// </summary>
    internal static partial class ReviewHybridLearningStore
    {
        const string Owner="HVX.PdReview.LocalHybridAi.v1";
        static readonly object gate=new object();

        // The four-argument name is also used by the report module through reflection.
        internal static string Add(string root,string kind,Dictionary<string,object> values,Dictionary<string,double[,]> matrices)
        {return AddGuarded(root,kind,values,matrices,null,null);}
        internal static string AddGuarded(string root,string kind,Dictionary<string,object> values,Dictionary<string,double[,]> matrices,Func<bool> allowed,object commitGate)
        {
            lock(gate)
            {
                if(allowed!=null&&!allowed())throw new OperationCanceledException();
                ValidateRoot(root,true);
                string id=DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff",CultureInfo.InvariantCulture)+"_"+Guid.NewGuid().ToString("N");
                string staging=Path.Combine(root,"staging",id),active=Path.Combine(root,"active",id);
                RejectReparse(staging);Directory.CreateDirectory(staging);
                File.WriteAllText(Path.Combine(staging,"owner.txt"),Owner,new UTF8Encoding(false));
                try
                {
                    var files=new Dictionary<string,object>(StringComparer.Ordinal);long totalCells=0;
                    if(matrices!=null)foreach(var pair in matrices)
                    {
                        if(allowed!=null&&!allowed())throw new OperationCanceledException();
                        if(pair.Value==null)continue;
                        long cells=(long)pair.Value.GetLength(0)*pair.Value.GetLength(1);totalCells+=cells;
                        if(cells>4000000L||totalCells>12000000L)throw new InvalidOperationException("Learning waveform payload is too large.");
                        string name=SafeName(pair.Key)+".csv",path=Path.Combine(staging,name);
                        WriteMatrix(path,pair.Value);files[pair.Key]=new Dictionary<string,object>{{"file",name},{"sha256",Hash(path)},{"rows",pair.Value.GetLength(0)},{"columns",pair.Value.GetLength(1)}};
                    }
                    var manifest=new Dictionary<string,object>(StringComparer.Ordinal)
                    {
                        {"owner",Owner},{"schema",1},{"transaction_id",id},{"kind",kind??""},
                        {"created_utc",DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture)},
                        {"training_status","pending_local_training"},{"production_model_modified",false},{"files",files}
                    };
                    if(values!=null)foreach(var pair in values)manifest[pair.Key]=pair.Value;
                    File.WriteAllText(Path.Combine(staging,"manifest.json"),new JavaScriptSerializer{MaxJsonLength=4*1024*1024}.Serialize(manifest),new UTF8Encoding(false));
                    RejectReparse(Path.GetDirectoryName(active));Directory.CreateDirectory(Path.GetDirectoryName(active));
                    lock(commitGate??gate){if(allowed!=null&&!allowed())throw new OperationCanceledException();Directory.Move(staging,active);}
                    return active;
                }
                catch
                {
                    if(Directory.Exists(staging))
                    {
                        string failed=Path.Combine(root,"history","failed_"+id);RejectReparse(Path.GetDirectoryName(failed));
                        Directory.CreateDirectory(Path.GetDirectoryName(failed));Directory.Move(staging,failed);
                    }
                    throw;
                }
            }
        }

        internal static Dictionary<string,object>[] Read(string root,string kind)
        {
            lock(gate)
            {
                if(!Directory.Exists(Path.Combine(root,"active")))return new Dictionary<string,object>[0];
                ValidateRoot(root,false);var result=new List<Dictionary<string,object>>();
                foreach(string directory in Directory.GetDirectories(Path.Combine(root,"active")).OrderBy(p=>p,StringComparer.Ordinal).Take(4096))
                {
                    if(!Owned(directory))continue;string manifestPath=Path.Combine(directory,"manifest.json");
                    int budget=4*1024*1024;
                    if(new FileInfo(manifestPath).Length>budget)continue;
                    Dictionary<string,object> item;
                    try{item=new JavaScriptSerializer{MaxJsonLength=budget}.Deserialize<Dictionary<string,object>>(File.ReadAllText(manifestPath,Encoding.UTF8));}
                    catch{continue;}
                    object value,owner,schema,id;int schemaNumber;
                    bool valid=item!=null&&item.TryGetValue("owner",out owner)&&String.Equals(Convert.ToString(owner,CultureInfo.InvariantCulture),Owner,StringComparison.Ordinal)&&
                        item.TryGetValue("schema",out schema)&&Int32.TryParse(Convert.ToString(schema,CultureInfo.InvariantCulture),NumberStyles.Integer,CultureInfo.InvariantCulture,out schemaNumber)&&schemaNumber==1&&item.TryGetValue("transaction_id",out id)&&
                        String.Equals(Convert.ToString(id,CultureInfo.InvariantCulture),Path.GetFileName(directory),StringComparison.Ordinal)&&item.TryGetValue("kind",out value)&&
                        String.Equals(Convert.ToString(value,CultureInfo.InvariantCulture),kind,StringComparison.Ordinal);
                    if(valid)result.Add(item);
                }
                return result.ToArray();
            }
        }

        internal static int Count(string root,string kind){return Read(root,kind).Length;}
        static bool Owned(string path)
        {
            string marker=Path.Combine(path,"owner.txt"),manifest=Path.Combine(path,"manifest.json");Guid id;string name=Path.GetFileName(path);
            return name.Length>33&&Guid.TryParseExact(name.Substring(name.Length-32),"N",out id)&&File.Exists(marker)&&File.Exists(manifest)&&File.ReadAllText(marker,Encoding.UTF8)==Owner;
        }
        static void ValidateRoot(string root,bool create)
        {
            if(String.IsNullOrWhiteSpace(root))throw new ArgumentException("Learning directory is empty.");
            root=Path.GetFullPath(root);RejectReparse(root);
            string drive=Path.GetPathRoot(root);if(String.Equals(root.TrimEnd('\\','/'),drive.TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase))throw new ArgumentException("A drive root cannot be a learning store.");
            string marker=Path.Combine(root,"owner.txt");
            if(File.Exists(marker)){if(File.ReadAllText(marker,Encoding.UTF8)!=Owner)throw new InvalidOperationException("The learning directory belongs to another application.");}
            else if(Directory.Exists(root)&&Directory.EnumerateFileSystemEntries(root).Any())throw new InvalidOperationException("Refusing to adopt an existing unowned learning directory.");
            else if(create){Directory.CreateDirectory(root);File.WriteAllText(marker,Owner,new UTF8Encoding(false));}
            RejectReparse(root);
        }
        static void RejectReparse(string path)
        {for(var current=new DirectoryInfo(Path.GetFullPath(path));current!=null;current=current.Parent)if(current.Exists&&(current.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Learning transactions cannot traverse a redirected directory.");}
        static string SafeName(string value)
        {var b=new StringBuilder();foreach(char c in value??"")if(Char.IsLetterOrDigit(c)||c=='-'||c=='_')b.Append(c);return b.Length==0?"matrix":b.ToString();}
        static void WriteMatrix(string path,double[,] values)
        {
            using(var writer=new StreamWriter(path,false,new UTF8Encoding(false)))
                for(int r=0;r<values.GetLength(0);r++){for(int c=0;c<values.GetLength(1);c++){if(c>0)writer.Write(',');writer.Write(values[r,c].ToString("R",CultureInfo.InvariantCulture));}writer.WriteLine();}
        }
        internal static string Hash(string path)
        {using(var sha=SHA256.Create())using(var stream=File.OpenRead(path))return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");}
    }

    public sealed partial class PdReviewControl
    {
        bool aiLearningMode;
        bool automaticConclusionsBeforeLearning=true;
        bool approximationBeforeLearning=true;
        string localAiLearningDirectory="",aiLearningStatus="";
        CheckBox aiLearningModeBox;
        Button localAiSuggestionButton,conclusionFeedbackButton;
        int learningEvaluationSequence=-1;
        int localAiPendingWrites;

        sealed class LocationLearningSnapshot
        {
            internal string Root;
            internal Dictionary<string,object> Values;
            internal Dictionary<string,double[,]> Matrices;
        }

        [Category("AI Learning"),DefaultValue(false),Description("Manual local-learning mode. Pauses automatic thresholding, analysis and PRPD recognition; conclusions follow manually accepted data.")]
        public bool AiLearningMode
        {
            get{return aiLearningMode;}
            set
            {
                if(value)RequireAiFeatures();
                if(aiLearningMode==value)return;
                if(IsAnalysisRunning)throw new InvalidOperationException(L("Stop the running analysis before changing AI learning mode.","请先停止正在运行的分析，再切换 AI 学习模式。"));
                aiLearningMode=value;learningEvaluationSequence=-1;
                if(value)
                {
                    approximationBeforeLearning=pdmap.ApproximateCablePositions;pdmap.ApproximateCablePositions=false;
                    automaticConclusionsBeforeLearning=spectrum.AutomaticConclusions;spectrum.AutomaticConclusions=false;
                    globalThreshold.Manual=true;foreach(var path in analysisTree==null?new string[0]:analysisTree.GetDataPaths())ThresholdFor(path).Manual=true;
                    recognitionGeneration++;if(recognitionUiTimer!=null)recognitionUiTimer.Stop();dischargeResult=null;
                    aiLearningStatus=L("AI learning mode: automatic acquisition analysis is paused; accept only manually verified selections. Conclusions follow accepted data.","AI 学习模式：自动采集分析已暂停，仅接受人工确认的选择；结论随已接受的数据更新，可修正与评分。");
                }
                else
                {
                    pdmap.ApproximateCablePositions=approximationBeforeLearning;
                    spectrum.AutomaticConclusions=automaticConclusionsBeforeLearning;QueueDischargeIdentification();
                    aiLearningStatus=L("Normal local algorithm + local AI review mode.","已恢复本地算法 + 本地轻量 AI 复核模式。");
                }
                RefreshApproximationProjection();SyncThresholdUi();RefreshWorkspaceUi();RefreshAiLearningUi();RefreshEvaluationUi();QueueAutomaticEvaluation();
            }
        }
        public void SetAiLearningMode(bool enabled){AiLearningMode=enabled;}
        [Category("AI Learning"),DefaultValue(""),Description("Owned append-only local learning directory. Empty uses LocalApplicationData.")]
        public string LocalAiLearningDirectory
        {get{return localAiLearningDirectory;}set{string path=(value??"").Trim();if(path.IndexOf('\0')>=0)throw new ArgumentException("Invalid learning path.");localAiLearningDirectory=path;}}
        [Browsable(false)] public string AiLearningStatus {get{return aiLearningStatus;}}
        [Browsable(false)] public int LocalAiLocationSampleCount {get{return SafeLearningCount("location_acceptance");}}
        [Browsable(false)] public int LocalAiConclusionFeedbackCount {get{return SafeLearningCount("conclusion_feedback");}}
        [Browsable(false)] public int LocalAiPendingWriteCount {get{return Math.Max(0,Interlocked.CompareExchange(ref localAiPendingWrites,0,0));}}

        string HybridLearningRoot()
        {
            if(!String.IsNullOrWhiteSpace(localAiLearningDirectory))return Path.GetFullPath(localAiLearningDirectory);
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"XIONGXIN","HVXUnifiedDesktop","LocalHybridAi");
        }
        int SafeLearningCount(string kind){if(!EnableAiFeatures)return 0;try{return ReviewHybridLearningStore.Count(HybridLearningRoot(),kind);}catch{return 0;}}

        void InitializeAiLearningModeUi()
        {
            aiLearningModeBox=new RoundedReviewToggle{Name="ReviewAiLearningMode",Appearance=Appearance.Button,TextAlign=ContentAlignment.MiddleCenter,Width=156,Height=30,Margin=new Padding(0,3,0,2),FlatStyle=FlatStyle.Flat};
            aiLearningModeBox.CheckedChanged+=(s,e)=>{if(uiSync)return;try{AiLearningMode=aiLearningModeBox.Checked;}catch(Exception ex){aiLearningStatus=ex.Message;RefreshAiLearningUi();}};
            side.Controls.Add(aiLearningModeBox);
            localAiSuggestionButton=ActionButton(152);localAiSuggestionButton.Name="ReviewLocalAiSuggestion";localAiSuggestionButton.Click+=(s,e)=>ShowLocalAiAnalysisSuggestion();side.Controls.Add(localAiSuggestionButton);
        }
        void InitializeConclusionFeedbackButton(Panel footer)
        {
            conclusionFeedbackButton=ActionButton(150);conclusionFeedbackButton.Name="ReviewConclusionFeedback";conclusionFeedbackButton.Dock=DockStyle.Right;
            conclusionFeedbackButton.Click+=(s,e)=>ShowConclusionFeedbackDialog();footer.Controls.Add(conclusionFeedbackButton);
        }
        void RefreshAiLearningUi()
        {
            if(aiLearningModeBox!=null)
            {
                bool prior=uiSync;uiSync=true;aiLearningModeBox.Checked=AiLearningMode;uiSync=prior;
                aiLearningModeBox.Text=AiLearningMode?L("AI learning: ON","AI 学习：已开启"):L("AI learning: OFF","AI 学习：已关闭");
                aiLearningModeBox.BackColor=AiLearningMode?Color.FromArgb(245,158,11):ReviewTheme.SurfaceAlt;
                aiLearningModeBox.ForeColor=AiLearningMode?Color.White:ReviewTheme.Ink;
                aiLearningModeBox.FlatAppearance.BorderColor=ReviewTheme.Border;
                aiLearningModeBox.Enabled=EnableAiFeatures&&!IsAnalysisRunning;
                aiLearningModeBox.AccessibleDescription=AiLearningMode?
                    L("Automatic and batch analysis are paused. Turn learning mode off to resume analysis.","自动与批量分析已暂停；关闭学习模式后可继续分析。"):
                    L("Learning mode is off. Checked files can be analysed normally.","学习模式已关闭，可以正常分析勾选的文件。");
            }
            if(localAiSuggestionButton!=null){localAiSuggestionButton.Text=ReviewWorkspaceText.Text(languageIndex,"Local AI suggestion","本地 AI 建议");localAiSuggestionButton.Enabled=EnableAiFeatures&&!IsAnalysisRunning&&selected>=0;}
            if(conclusionFeedbackButton!=null)
            {
                conclusionFeedbackButton.Text=ReviewWorkspaceText.Text(languageIndex,"Correct and rate","修正与评分");
                conclusionFeedbackButton.Visible=EnableAiFeatures&&AiLearningMode&&LatestEvaluationResult!=null&&learningEvaluationSequence==LatestEvaluationResult.RequestSequence;
                conclusionFeedbackButton.Enabled=conclusionFeedbackButton.Visible;
            }
            if(layoutReady)ApplyReadableWorkspaceLayout();
        }

        string LocationPathForRow(int row)
        {
            if(analysisTree==null||row<0||row>=SourceRowCount)return overviewPath??"";
            string[] paths=analysisTree.GetDataPaths();double[,] catalog=analysisTree.GetDataCatalog();
            for(int i=0;i<paths.Length&&i<catalog.GetLength(0);i++)if(catalog.GetLength(1)>1&&catalog[i,0]==working[row,0]&&catalog[i,1]==working[row,1])return paths[i];
            return overviewPath??"";
        }
        static double Clean(double value,double divisor=1)
        {if(!Finite(value)||!Finite(divisor)||divisor==0)return 0;return Math.Max(-4,Math.Min(4,value/divisor));}
        static double CurveValue(double[,] curve,int index)
        {return curve!=null&&index>=0&&index<curve.GetLength(0)&&curve.GetLength(1)>1&&Finite(curve[index,1])?curve[index,1]:double.NaN;}
        static double CurveScale(double[,] curve)
        {double max=0;if(curve!=null&&curve.GetLength(1)>1)for(int r=0;r<curve.GetLength(0);r++)if(Finite(curve[r,1]))max=Math.Max(max,Math.Abs(curve[r,1]));return max>1e-15?max:1;}
        static void AddFeature(List<double> values,List<bool> missing,double value,double divisor)
        {bool absent=!Finite(value)||!Finite(divisor)||divisor==0;missing.Add(absent);values.Add(absent?0:Clean(value,divisor));}
        static void AddWaveformWindow(List<double> values,List<bool> missing,double[,] curve,int center,int width)
        {
            int half=width/2;double scale=0;
            if(curve!=null&&curve.GetLength(1)>1)for(int i=center-half;i<center-half+width;i++)if(i>=0&&i<curve.GetLength(0)&&Finite(curve[i,1]))scale=Math.Max(scale,Math.Abs(curve[i,1]));
            for(int i=center-half;i<center-half+width;i++)
            {
                bool absent=curve==null||curve.GetLength(1)<2||i<0||i>=curve.GetLength(0)||!Finite(curve[i,1])||scale<=1e-15;
                missing.Add(absent);values.Add(absent?0:Math.Max(-1,Math.Min(1,curve[i,1]/scale)));
            }
        }
        double[] CurrentLocalAiFeatures(out double[,] raw,out double[,] calibration,out bool[] missingMask)
        {
            raw=pdmap.GetReportCurve(0);calibration=pdmap.GetReportCurve(1);double[] row=pdmap.GetReviewParameterRow();
            int incident=pdmap.CurrentIncidentIndex,reflection=pdmap.CurrentReflectionIndex;double rawScale=CurveScale(raw),calScale=CurveScale(calibration),sampleNs=pdmap.CurrentSampleIntervalNs;
            double roundTripNs=Finite(row[5])&&Finite(row[6])&&row[6]>0?2*row[5]/row[6]*1000:double.NaN;
            var values=new List<double>();var missing=new List<bool>();
            AddFeature(values,missing,Finite(row[5])?Math.Log(1+Math.Max(0,row[5])):double.NaN,Math.Log(2001));AddFeature(values,missing,row[6],250);
            AddFeature(values,missing,Finite(row[15])?Math.Log(1+Math.Abs(row[15])):double.NaN,Math.Log(1000001));AddFeature(values,missing,row[2],100);AddFeature(values,missing,row[3],360);
            AddFeature(values,missing,sampleNs,100);AddFeature(values,missing,incident,Math.Max(1,raw.GetLength(0)-1));
            AddFeature(values,missing,Finite(sampleNs)?(reflection-incident)*sampleNs:double.NaN,roundTripNs);AddFeature(values,missing,CurveValue(raw,incident),rawScale);AddFeature(values,missing,CurveValue(raw,reflection),rawScale);
            AddFeature(values,missing,CurveValue(calibration,incident),calScale);AddFeature(values,missing,CurveValue(calibration,reflection),calScale);AddFeature(values,missing,row[23],5000);AddFeature(values,missing,row[24],10000000);AddFeature(values,missing,row[33],1);
            // Shape windows are normalized independently so distance reflects incident,
            // reflection and calibration morphology rather than absolute gain alone.
            AddWaveformWindow(values,missing,raw,incident,24);AddWaveformWindow(values,missing,raw,reflection,24);
            AddWaveformWindow(values,missing,calibration,incident,24);AddWaveformWindow(values,missing,calibration,reflection,24);
            missingMask=missing.ToArray();return values.ToArray();
        }
        static object[] Box(double[] values){return values.Select(v=>(object)(Finite(v)?v:0)).ToArray();}
        static object[] Box(bool[] values){return values.Select(v=>(object)v).ToArray();}
        static double[] Numbers(object value)
        {
            var result=new List<double>();var enumerable=value as IEnumerable;if(enumerable==null||value is string)return result.ToArray();
            foreach(object item in enumerable){double number;if(Double.TryParse(Convert.ToString(item,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out number)&&Finite(number))result.Add(number);else result.Add(0);}
            return result.ToArray();
        }
        static double Number(Dictionary<string,object> data,string key,double fallback=double.NaN)
        {object value;double number;return data!=null&&data.TryGetValue(key,out value)&&Double.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out number)&&Finite(number)?number:fallback;}
        static bool Flag(Dictionary<string,object> data,string key,bool fallback=false)
        {object value;bool flag;return data!=null&&data.TryGetValue(key,out value)&&Boolean.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),out flag)?flag:fallback;}
        static bool[] Flags(object value,int length)
        {var result=new bool[length];var enumerable=value as IEnumerable;if(enumerable==null||value is string)return result;int i=0;foreach(object item in enumerable){if(i>=length)break;bool flag;result[i++]=Boolean.TryParse(Convert.ToString(item,CultureInfo.InvariantCulture),out flag)&&flag;}return result;}
        static string ManifestText(Dictionary<string,object> data,string key)
        {object value;return data!=null&&data.TryGetValue(key,out value)?Convert.ToString(value,CultureInfo.InvariantCulture)??"":"";}
        static double Distance(double[] left,bool[] leftMissing,double[] right,bool[] rightMissing)
        {if(left.Length==0||left.Length!=right.Length)return double.PositiveInfinity;double sum=0;int used=0;for(int i=0;i<left.Length;i++){if(i<leftMissing.Length&&leftMissing[i]||i<rightMissing.Length&&rightMissing[i])continue;double d=left[i]-right[i];sum+=d*d;used++;}return used<Math.Max(8,left.Length/2)?double.PositiveInfinity:Math.Sqrt(sum/used);}
        static double Distance(double[] left,double[] right)
        {return Distance(left,new bool[left==null?0:left.Length],right,new bool[right==null?0:right.Length]);}

        LocationLearningSnapshot CaptureAcceptedLocationLearningSample(int rowIndex,double[] acceptedRow)
        {
            if(!EnableAiFeatures||!AiLearningMode||rowIndex<0||acceptedRow==null||IsAcquisitionReferenceOnly(rowIndex))return null;
            try
            {
                double[,] raw,calibration;bool[] missing;double[] features=CurrentLocalAiFeatures(out raw,out calibration,out missing);double[] row=pdmap.GetReviewParameterRow();
                string path=LocationPathForRow(rowIndex);var threshold=ThresholdFor(path);bool thresholdConfirmed=threshold.Manual&&threshold.HumanConfirmed;
                var values=new Dictionary<string,object>{{"source_project",projectPath??""},{"source_location_file",path},{"source_row",rowIndex},{"operator",Environment.UserName},
                    {"feature_schema","location-v2-shape24"},{"feature_vector",Box(features)},{"feature_missing_mask",Box(missing)},{"threshold_human_confirmed",thresholdConfirmed},{"threshold_source",thresholdConfirmed?"manual_confirmed":"not_labelled"},
                    {"incident_index",pdmap.CurrentIncidentIndex},{"reflection_index",pdmap.CurrentReflectionIndex},{"reflection_delay_ns",(pdmap.CurrentReflectionIndex-pdmap.CurrentIncidentIndex)*pdmap.CurrentSampleIntervalNs},
                    {"cable_length_m",row[5]},{"velocity_m_per_us",row[6]},{"sample_interval_ns",pdmap.CurrentSampleIntervalNs},{"selected_calibration_path",pdmap.SelectedCalibrationPath??""},
                    {"calibration_status",pdmap.ProjectCalibrationStatusCode},{"calibration_sample_rate_hz",pdmap.SelectedCalibrationSampleRateHz},{"human_decision","accepted"},{"control_build",ControlBuildVersion},{"analysis_algorithm_index",AnalysisAlgorithmIndex},{"connecting_cable_length_m",ConnectingCableLengthM}};
                if(thresholdConfirmed)values["threshold_pc"]=threshold.Value;
                values["raw_location_m"]=pdmap.RawLocationM;values["effective_location_m"]=ProjectCanonicalAccessoryLocation(rowIndex,acceptedRow[13]);
                values["position_projection_policy"]=PositionProjectionPolicy;values["automatic_joint_snap"]=false;
                var matrices=new Dictionary<string,double[,]>{{"source-parameter-row",ToMatrix(rowIndex<original.GetLength(0)?Row(original,rowIndex):row)},{"accepted-parameter-row",ToMatrix(acceptedRow)},{"raw-pulse",raw},{"filtered-pulse",pdmap.GetReportCurve(3)},
                    {"aligned-calibration",calibration},{"filtered-calibration",pdmap.GetReportCurve(4)},{"damping",pdmap.GetReportCurve(2)}};
                return new LocationLearningSnapshot{Root=HybridLearningRoot(),Values=values,Matrices=matrices};
            }
            catch(Exception ex){aiLearningStatus=L("Location was accepted, but its learning sample could not be captured: ","定位已接受，但学习样本捕获失败：")+ex.Message;SetReviewOperationStatus(aiLearningStatus);RefreshAiLearningUi();return null;}
        }
        void QueueAcceptedLocationLearningSample(LocationLearningSnapshot snapshot)
        {
            if(snapshot==null||!EnableAiFeatures)return;
            int generation=Interlocked.CompareExchange(ref aiFeatureGeneration,0,0);
            Interlocked.Increment(ref localAiPendingWrites);
            aiLearningStatus=L("Saving the accepted local learning sample…","正在保存已接受的本地学习样本…");SetReviewOperationStatus(aiLearningStatus);RefreshAiLearningUi();
            Task.Run(()=>
            {
                if(!CanWriteAiSample(generation))return null;
                object pathValue;string path;
                if(snapshot.Values.TryGetValue("selected_calibration_path",out pathValue)&&(path=Convert.ToString(pathValue,CultureInfo.InvariantCulture))!=null&&File.Exists(path))snapshot.Values["calibration_sha256"]=ReviewHybridLearningStore.Hash(path);
                if(snapshot.Values.TryGetValue("source_location_file",out pathValue)&&(path=Convert.ToString(pathValue,CultureInfo.InvariantCulture))!=null&&File.Exists(path))snapshot.Values["source_location_sha256"]=ReviewHybridLearningStore.Hash(path);
                return ReviewHybridLearningStore.AddGuarded(snapshot.Root,"location_acceptance",snapshot.Values,snapshot.Matrices,()=>CanWriteAiSample(generation),aiFeatureCommitGate);
            }).ContinueWith(task=>
            {
                Interlocked.Decrement(ref localAiPendingWrites);
                if(task.IsFaulted){var observed=task.Exception;}
                if(!CanWriteAiSample(generation))return;
                string message=task.IsFaulted?L("Location was accepted, but its learning sample could not be recorded: ","定位已接受，但学习样本记录失败：")+task.Exception.GetBaseException().Message:
                    L("Accepted location recorded as a local learning sample; no production model was changed.","已将人工接受的定位记录为本地学习样本；生产模型未被直接修改。");
                if(IsDisposed||Disposing)return;
                Action update=()=>{if(!CanWriteAiSample(generation))return;aiLearningStatus=message;SetReviewOperationStatus(message);RefreshAiLearningUi();};
                try{BeginInvoke(update);}catch(InvalidOperationException){}
            },TaskScheduler.Default);
        }
        static double[,] ToMatrix(double[] values){var result=new double[1,values==null?0:values.Length];if(values!=null)for(int c=0;c<values.Length;c++)result[0,c]=values[c];return result;}

        public LocalAiAnalysisSuggestion GetLocalAiAnalysisSuggestion()
        {
            if(!EnableAiFeatures)return new LocalAiAnalysisSuggestion(false,0,0,double.NaN,-1,AiFeaturesDisabledMessage());
            if(selected<0)return new LocalAiAnalysisSuggestion(false,0,0,double.NaN,-1,L("Select a MAP first.","请先选择一个 MAP。"));
            try
            {
                double[,] raw,cal;bool[] currentMissing;double[] current=CurrentLocalAiFeatures(out raw,out cal,out currentMissing);var candidates=new List<Tuple<double,Dictionary<string,object>>>();double sampleNs=pdmap.CurrentSampleIntervalNs;
                foreach(var item in ReviewHybridLearningStore.Read(HybridLearningRoot(),"location_acceptance"))
                {object value,maskValue;if(!String.Equals(ManifestText(item,"feature_schema"),"location-v2-shape24",StringComparison.Ordinal)||!item.TryGetValue("feature_vector",out value))continue;double storedNs=Number(item,"sample_interval_ns");if(!Finite(sampleNs)||!Finite(storedNs)||sampleNs<=0||Math.Abs(storedNs-sampleNs)>sampleNs*.10)continue;double[] feature=Numbers(value);item.TryGetValue("feature_missing_mask",out maskValue);double distance=Distance(current,currentMissing,feature,Flags(maskValue,feature.Length));if(Finite(distance))candidates.Add(Tuple.Create(distance,item));}
                int total=candidates.Count;if(total<3)return new LocalAiAnalysisSuggestion(false,total,0,double.NaN,-1,L("At least three local confirmed samples are required.","至少需要 3 个本地人工确认样本。"));
                var nearest=candidates.OrderBy(x=>x.Item1).Take(5).ToArray();if(nearest[0].Item1>.80)return new LocalAiAnalysisSuggestion(false,total,0,double.NaN,-1,L("The current waveform is outside the local sample distribution; no suggestion was applied.","当前波形超出本地样本分布，未给出建议。"));
                double sum=0,threshold=0,thresholdWeight=0,delayNs=0,averageDistance=0;
                foreach(var sample in nearest){double weight=1/(.05+sample.Item1);sum+=weight;averageDistance+=sample.Item1;double storedDelay=Number(sample.Item2,"reflection_delay_ns",double.NaN);if(Finite(storedDelay))delayNs+=weight*storedDelay;if(Flag(sample.Item2,"threshold_human_confirmed")){double storedThreshold=Number(sample.Item2,"threshold_pc",double.NaN);if(Finite(storedThreshold)){threshold+=weight*storedThreshold;thresholdWeight+=weight;}}}
                if(sum<=0)return new LocalAiAnalysisSuggestion(false,total,0,double.NaN,-1,L("Local samples are incompatible with this waveform.","本地样本与当前波形不兼容。"));
                int reflection=pdmap.CurrentIncidentIndex+(int)Math.Round(delayNs/sum/sampleNs);reflection=Math.Max(0,Math.Min(Math.Max(0,raw.GetLength(0)-1),reflection));
                double confidence=Math.Min(.95,(double)total/(total+3)*1/(1+averageDistance/nearest.Length));
                return new LocalAiAnalysisSuggestion(true,total,confidence,thresholdWeight>0?threshold/thresholdWeight:double.NaN,reflection,L("Local k-nearest-neighbour suggestion; human confirmation is required.","本地轻量近邻建议；必须由人工确认。"));
            }
            catch(Exception ex){return new LocalAiAnalysisSuggestion(false,0,0,double.NaN,-1,CustomerFailure("LocalAiSuggestionFailed",ex.ToString(),"The local suggestion is unavailable. Check local sample storage, or review the waveform manually.","本地建议暂不可用。请检查本地样本保存位置，或手动复核波形。"));}
        }
        public bool ApplyLocalAiAnalysisSuggestion()
        {
            if(!EnableAiFeatures)return FinishLocalAiSuggestion(false,AiFeaturesDisabledMessage());
            if(AiLearningMode)return FinishLocalAiSuggestion(false,L("Learning mode records manual choices and does not apply AI suggestions.","学习模式只记录人工选择，不应用 AI 建议。"));
            return ApplyLocalAiAnalysisSuggestionCore(GetLocalAiAnalysisSuggestion());
        }
        bool FinishLocalAiSuggestion(bool changed,string message)
        {
            aiLearningStatus=message??"";SetReviewOperationStatus(aiLearningStatus);
            if(localAiSuggestionButton!=null)localAiSuggestionButton.AccessibleDescription=aiLearningStatus;
            return changed;
        }
        string LocalAiMoveMessage(LabVIEWPdMap.ReflectionMoveResult result,bool thresholdChanged)
        {
            switch(result){
                case LabVIEWPdMap.ReflectionMoveResult.UnsupportedMode:return L("No change: reflection suggestions are not supported in capacitor statistics mode.","未更改：电容统计模式不支持反射波建议。");
                case LabVIEWPdMap.ReflectionMoveResult.NoWaveform:return L("No change: no waveform is loaded.","未更改：尚未加载波形。");
                case LabVIEWPdMap.ReflectionMoveResult.SelectionUnavailable:return L("No change: the suggested reflection is outside the available selection window. Check cable geometry and sampling rate.","未更改：建议反射点不在可用选取范围内，请核对电缆几何和采样率。");
                case LabVIEWPdMap.ReflectionMoveResult.AlreadyAtTarget:return thresholdChanged?L("Already at the suggested point; only the suggested threshold was updated for review. No automatic acceptance.","已位于建议点；仅更新了建议阈值供复核，未自动接受结果。"):L("Already at the suggested point; nothing was changed or accepted automatically.","已位于建议点；未更改，也未自动接受结果。");
                default:return L("The reflection cursor moved to the local suggestion for review; the result was not accepted automatically.","反射游标已移动至本地建议点供复核，未自动接受结果。");
            }
        }
        bool ApplyLocalAiAnalysisSuggestionCore(LocalAiAnalysisSuggestion suggestion)
        {
            if(!EnableAiFeatures)return FinishLocalAiSuggestion(false,AiFeaturesDisabledMessage());
            if(AiLearningMode)return FinishLocalAiSuggestion(false,L("Learning mode records manual choices and does not apply AI suggestions.","学习模式只记录人工选择，不应用 AI 建议。"));
            if(IsAnalysisRunning)return FinishLocalAiSuggestion(false,L("No change: wait for the running analysis to finish.","未更改：请等待正在运行的分析完成。"));
            if(suggestion==null||!suggestion.Available)return FinishLocalAiSuggestion(false,suggestion==null?L("No local suggestion is available.","暂无本地建议。"):suggestion.Status);
            var move=pdmap.MoveReflectionToIndexWithResult(suggestion.SuggestedReflectionIndex);
            if(move!=LabVIEWPdMap.ReflectionMoveResult.Moved&&move!=LabVIEWPdMap.ReflectionMoveResult.AlreadyAtTarget)return FinishLocalAiSuggestion(false,LocalAiMoveMessage(move,false));
            bool thresholdChanged=false;string path=LocationPathForRow(selected);
            if(Finite(suggestion.SuggestedThresholdPc)&&suggestion.SuggestedThresholdPc>=0&&suggestion.SuggestedThresholdPc<=1e9){
                var choice=ThresholdFor(path);
                thresholdChanged=!choice.Manual||choice.Value!=suggestion.SuggestedThresholdPc;
                if(thresholdChanged){SetFileAnalysisThreshold(path,true,suggestion.SuggestedThresholdPc);ThresholdFor(path).HumanConfirmed=false;}
            }
            return FinishLocalAiSuggestion(move==LabVIEWPdMap.ReflectionMoveResult.Moved||thresholdChanged,LocalAiMoveMessage(move,thresholdChanged));
        }
        void ShowLocalAiAnalysisSuggestion()
        {
            if(!EnableAiFeatures)return;
            var suggestion=GetLocalAiAnalysisSuggestion();string text=LocalAiSuggestionText(suggestion);
            if(suggestion.Available){if(Finite(suggestion.SuggestedThresholdPc))text+=Environment.NewLine+L("Suggested threshold: ","建议阈值：")+suggestion.SuggestedThresholdPc.ToString("0.###",CultureInfo.InvariantCulture)+" pC";
                }
            if(!suggestion.Available||AiLearningMode){MessageBox.Show(FindForm(),text,L("Local AI suggestion","本地 AI 建议"),MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
            if(MessageBox.Show(FindForm(),text+Environment.NewLine+Environment.NewLine+L("Apply for manual review?","应用该建议供人工复核？"),L("Local AI suggestion","本地 AI 建议"),MessageBoxButtons.YesNo,MessageBoxIcon.Question)==DialogResult.Yes){
                // Apply exactly the suggestion the operator saw, without a second
                // synchronous store read or silently replacing the proposed pick.
                bool changed=ApplyLocalAiAnalysisSuggestionCore(suggestion);
                MessageBox.Show(FindForm(),AiLearningStatus,L("Local AI suggestion","本地 AI 建议"),MessageBoxButtons.OK,changed?MessageBoxIcon.Information:MessageBoxIcon.Warning);
            }
        }

        static double[] ConclusionFeatures(ReviewEvaluationResult result)
        {
            double[,] matrix=result.LocationMatrix;double charge=0,position=0,length=0,voltage=0;int count=0;
            for(int r=0;r<matrix.GetLength(0);r++){if(matrix.GetLength(1)>15&&Finite(matrix[r,15]))charge+=Math.Abs(matrix[r,15]);if(matrix.GetLength(1)>13&&Finite(matrix[r,13]))position+=matrix[r,13];if(matrix.GetLength(1)>5&&Finite(matrix[r,5]))length+=matrix[r,5];if(matrix.GetLength(1)>2&&Finite(matrix[r,2]))voltage+=matrix[r,2];count++;}
            double n=Math.Max(1,count);return new[]{Clean(result.OperatingVoltageKv,40),Clean(Math.Log(1+charge/n),Math.Log(1000001)),Clean(position/n,Math.Max(1,length/n)),Clean(length/n,2000),Clean(voltage/n,100),Clean(result.ValidPulseCount,1000),Clean(result.InputRowCount,1000),Clean(result.EndpointProjectedRowCount,100)};
        }
        public LocalAiConclusionSuggestion GetLocalAiConclusionSuggestion()
        {
            if(!EnableAiFeatures)return new LocalAiConclusionSuggestion(false,0,0,"",AiFeaturesDisabledMessage());
            var result=LatestEvaluationResult;if(result==null)return new LocalAiConclusionSuggestion(false,0,0,"",L("Generate a current conclusion first.","请先生成当前结论。"));
            try
            {
                double[] current=ConclusionFeatures(result);var candidates=new List<Tuple<double,double,string>>();
                foreach(var item in ReviewHybridLearningStore.Read(HybridLearningRoot(),"conclusion_feedback"))
                {object value;if(!item.TryGetValue("feature_vector",out value))continue;double[] feature=Numbers(value);double rating=Number(item,"rating",0);string corrected=ManifestText(item,"corrected_conclusion");double distance=Distance(current,feature);if(Finite(distance)&&rating>=1&&!String.IsNullOrWhiteSpace(corrected))candidates.Add(Tuple.Create(distance,rating,corrected));}
                if(candidates.Count<3)return new LocalAiConclusionSuggestion(false,candidates.Count,0,"",L("At least three rated local corrections are required.","至少需要 3 条已评分的本地结论修正。"));
                var best=candidates.OrderBy(x=>x.Item1/(1+(5-x.Item2)*.10)).First();double confidence=Math.Min(.9,(double)candidates.Count/(candidates.Count+4)*1/(1+best.Item1));
                return new LocalAiConclusionSuggestion(true,candidates.Count,confidence,best.Item3,L("Nearest rated local correction; the original analysis remains the primary conclusion.","最相近的本地已评分修正；原分析结果仍是主结论。"));
            }
            catch(Exception ex){return new LocalAiConclusionSuggestion(false,0,0,"",CustomerFailure("LocalAiConclusionSuggestionFailed",ex.ToString(),"The local reference is unavailable. Check local sample storage, or review the conclusion manually.","本地参考暂不可用。请检查本地样本保存位置，或手动复核结论。"));}
        }
        public bool RecordConclusionFeedback(string correctedConclusion,int rating,string notes)
        {
            if(!EnableAiFeatures){aiLearningStatus=AiFeaturesDisabledMessage();return false;}
            if(!AiLearningMode){aiLearningStatus=L("Enable AI learning mode before recording conclusion feedback.","请先启用 AI 学习模式，再记录结论反馈。");return false;}
            var result=LatestEvaluationResult;if(result==null||learningEvaluationSequence!=result.RequestSequence){aiLearningStatus=L("Generate a current conclusion in learning mode first.","请先在学习模式中生成当前结论。");return false;}
            if(HasAcquisitionRiskInMatrix(result.LocationMatrix)){aiLearningStatus=AcquisitionRiskText();SetReviewOperationStatus(aiLearningStatus);return false;}
            string corrected=(correctedConclusion??"").Trim(),comment=(notes??"").Trim();if(corrected.Length==0||corrected.Length>20000||comment.Length>10000||rating<1||rating>5){aiLearningStatus=L("Correction, rating or notes are invalid.","修正结论、评分或备注无效。");return false;}
            try
            {
                var values=new Dictionary<string,object>{{"source_project",projectPath??""},{"operator",Environment.UserName},{"feature_schema","conclusion-v1"},{"feature_vector",Box(ConclusionFeatures(result))},
                    {"algorithm","Task15 local"},{"algorithm_api",result.Api},{"algorithm_conclusion",result.SummaryText},{"corrected_conclusion",corrected},{"rating",rating},{"notes",comment},
                    {"rating_scale","1=wrong_or_risky;2=major_correction;3=usable_after_moderate_correction;4=minor_correction;5=no_correction"},
                    {"operating_voltage_kv",result.OperatingVoltageKv},{"input_rows",result.InputRowCount},{"valid_pulses",result.ValidPulseCount},
                    {"service_years",spectrum.ConclusionCableServiceYears},{"cable_body_type",spectrum.ConclusionCableBodyType},{"joint_type",spectrum.ConclusionIntermediateJointType},{"control_build",ControlBuildVersion}};
                try{string runtime=SpectrumTask15Bridge.ResolvePath(spectrum.Task15RuntimePath);values["task15_runtime_sha256"]=ReviewHybridLearningStore.Hash(runtime);}catch{}
                ReviewHybridLearningStore.Add(HybridLearningRoot(),"conclusion_feedback",values,new Dictionary<string,double[,]>{{"task15-location-matrix",result.LocationMatrix}});
                aiLearningStatus=L("Conclusion correction and rating recorded locally; the production model was not changed.","结论修正与评分已保存在本地；生产模型未被直接修改。");SetReviewOperationStatus(aiLearningStatus);RefreshAiLearningUi();return true;
            }
            catch(Exception ex){aiLearningStatus=CustomerFailure("ConclusionFeedbackSaveFailed",ex.ToString(),"The feedback was not saved. Check local storage access and retry.","反馈未保存。请检查本地保存位置是否可写后重试。");SetReviewOperationStatus(aiLearningStatus);return false;}
        }
        void ShowConclusionFeedbackDialog()
        {
            if(!EnableAiFeatures)return;
            var result=LatestEvaluationResult;if(result==null)return;
            using(var form=CreateConclusionFeedbackDialog(result.SummaryText))form.ShowDialog(FindForm());
        }
        Form CreateConclusionFeedbackDialog(string conclusion)
        {
            var form=ArchiveDialog(L("Correct and rate conclusion","结论修正与评分"),new Size(720,570));
            form.MinimumSize=new Size(480,340);
            {
                var footer=new FlowLayoutPanel{Name="ReviewFeedbackActions",Dock=DockStyle.Bottom,Height=44,FlowDirection=FlowDirection.RightToLeft,Padding=new Padding(4)};
                var cancel=ActionButton(90);cancel.Text=L("Cancel","取消");cancel.DialogResult=DialogResult.Cancel;
                var save=ActionButton(130);save.Text=L("Save feedback","保存反馈");ReviewTheme.ButtonRole(save,true);
                var rating=new NumericUpDown{Name="ReviewConclusionRating",Minimum=1,Maximum=5,Value=5,Width=64};
                footer.Controls.Add(cancel);footer.Controls.Add(save);footer.Controls.Add(new Label{Text=L("Rating 1–5","评分 1–5"),AutoSize=true,Padding=new Padding(0,6,2,0)});footer.Controls.Add(rating);
                var notes=new TextBox{Name="ReviewConclusionFeedbackNotes",Dock=DockStyle.Fill,Multiline=true,Height=78,ScrollBars=ScrollBars.Vertical};
                var notesLabel=new Label{AutoSize=true,Text=L("Notes (optional)","备注（可选）")};
                var corrected=new TextBox{Name="ReviewCorrectedConclusion",Dock=DockStyle.Fill,Multiline=true,Height=120,ScrollBars=ScrollBars.Vertical,Text=conclusion};
                var correctedLabel=new Label{AutoSize=true,Text=L("Human-corrected conclusion","人工修正结论")};
                var original=new TextBox{Name="ReviewAlgorithmConclusion",Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,Height=130,ScrollBars=ScrollBars.Vertical,Text=conclusion,BackColor=Color.FromArgb(245,248,250)};
                var originalLabel=new Label{AutoSize=true,Text=L("Original analysis conclusion","原分析结论")};
                var scale=new Label{AutoSize=true,ForeColor=ReviewTheme.SecondaryInk,Text=L("Score the original conclusion: 1 wrong/risky; 2 major correction; 3 usable after moderate correction; 4 minor correction; 5 no correction.","请评价原结论：1 完全错误或有风险；2 重大修正；3 中度修正后可用；4 轻微修正；5 无需修正。")};
                var viewport=new Panel{Name="ReviewFeedbackViewport",Dock=DockStyle.Fill,AutoScroll=true};
                var content=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,RowCount=7,Padding=new Padding(0,0,8,8)};
                content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
                Control[] fields={scale,originalLabel,original,correctedLabel,corrected,notesLabel,notes};
                for(int i=0;i<fields.Length;i++){content.RowStyles.Add(new RowStyle(SizeType.AutoSize));fields[i].Anchor=AnchorStyles.Left|AnchorStyles.Right;content.Controls.Add(fields[i],0,i);}
                viewport.Controls.Add(content);form.Controls.Add(viewport);form.Controls.Add(footer);form.CancelButton=cancel;
                Action fit=()=>{
                    int width=Math.Max(80,content.ClientSize.Width-content.Padding.Horizontal-6);
                    foreach(var label in new[]{scale,originalLabel,correctedLabel,notesLabel})label.MaximumSize=new Size(width,0);
                    original.Height=Math.Max(100,original.Font.Height*4+16);corrected.Height=Math.Max(100,corrected.Font.Height*4+16);notes.Height=Math.Max(66,notes.Font.Height*3+12);
                };
                content.SizeChanged+=(s,e)=>fit();foreach(var field in fields)field.FontChanged+=(s,e)=>fit();form.Shown+=(s,e)=>fit();
                ReviewTheme.BindDialogFooter(form,footer);
                ReviewTheme.ReserveDialogBody(form,footer,new Size(480,340));
                save.Click+=(s,e)=>{if(RecordConclusionFeedback(corrected.Text,(int)rating.Value,notes.Text)){form.DialogResult=DialogResult.OK;form.Close();}else MessageBox.Show(form,AiLearningStatus,form.Text,MessageBoxButtons.OK,MessageBoxIcon.Warning);};
                return form;
            }
        }
    }
}
