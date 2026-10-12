using System;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LabVIEWPdMap;
using HVX.UnifiedSpectrum;

namespace HVX.PdReview
{
    /// <summary>One cached source-row identity for the waveform, accepted results and review UI.</summary>
    [ToolboxItem(true)]
    public sealed partial class PdReviewControl : UserControl
    {
        readonly PdMapControl pdmap = new PdMapControl();
        readonly UnifiedSpectrumControl spectrum = new UnifiedSpectrumControl();
        double[,] working = new double[0,36], matching = new double[0,0];
        double[,] original = new double[0,36];
        int[] confidenceSources = new int[0];
        int selected = -1;
        int previewFilteredRow = -1;
        bool synchronizing, english, loadingData, pendingPreviewNotification;
        string projectPath = "";

        public PdReviewControl()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
            BackColor=ReviewTheme.Workspace; ForeColor=ReviewTheme.Ink;var ownedFont=ReviewTheme.RegularFont();Font=ownedFont;Disposed+=(s,e)=>ownedFont.Dispose();
            Size=DesignReferenceSize;
            pdmap.Font=Font;pdmap.InformationFontSize=9.5F;pdmap.CompactReviewInformation=true;spectrum.Font=Font;
            pdmap.ShowCalibrationCurve=true;pdmap.ShowDampingCurve=true;pdmap.ApproximateCablePositions=true;
            pdmap.AutomaticPositionApproximationAllowed=()=>!AiLearningMode;
            pdmap.ExactReviewPositionRequired=IsExactManualPositionRow;
            pdmap.ExactReviewPositionSelected=RememberExactManualPosition;
            spectrum.AcceptedOnly=true;
            // Conclusions belong to the report stage, never waveform review or refresh.
            spectrum.AutomaticConclusionsGate=()=>false;
            spectrum.AutomaticConclusions=false;
            spectrum.ShowConclusionAnnotations=false;
            InitializeLayout();
            InitializeNoiseReferenceUi();
            InitializeReportChartExports();
            InitializeReviewRangeActions();
            InitializeConnectingGeometry();
            InitializeCableJointEditing();
            pdmap.AnalysisEdited += OnAnalysisEdited;
            InitializeApproximationLink();
            spectrum.PdMapSelected += (s,e)=>ViewSourceRow(spectrum.SelectedSourceRowIndex);
            spectrum.RegionActionCompleted += OnSpectrumRegionAction;
            // ABC toggles and plot modes affect the spectrum presentation only.
            // Only statistics masks may move the waveform's review selection.
            spectrum.ViewChanged += (s,e)=>{if(!synchronizing&&!spectrum.IsCameraViewNotification){spectrum.HighlightSourceRow(selected);RefreshReviewUi();}};
            RestoreAnalysisPreferences();
            InitializeDischargeRecognitionUi();
            RefreshReviewUi();
            InitializeAnalysisDirtyTracking();
            InitializeAutomaticEvaluation();
            // InitializeLayout and the constructor above have applied the
            // initial language. Do not repeat that work on the first same-
            // language host assignment, but retain real language changes.
            appliedLanguageIndex = languageIndex;
        }

        [Browsable(false)] public PdMapControl PdMap { get { return pdmap; } }
        [Browsable(false)] public UnifiedSpectrumControl Spectrum { get { return spectrum; } }
        static readonly string controlBuildVersion=((System.Reflection.AssemblyFileVersionAttribute)Attribute.GetCustomAttribute(typeof(PdReviewControl).Assembly,typeof(System.Reflection.AssemblyFileVersionAttribute))).Version;
        [Browsable(false)] public string ControlBuildVersion { get { return controlBuildVersion; } }
        static string ControlBuildLabel {get{
#if DIRECT_DEBUG_20261010
            return "Analysis "+controlBuildVersion+" · debug d01";
#else
            return "HV-DAC Xpert "+controlBuildVersion;
#endif
        }}
        [Browsable(false)] public string LastError { get; private set; } = "";
        [Browsable(false)] public int ResultChangeSequence { get; private set; }
        [Browsable(false)] public int SelectionSequence { get; private set; }
        [Browsable(false)] public int SourceRowCount { get { return working.GetLength(0); } }
        [Browsable(false)] public int SelectedSourceRowIndex { get { return selected; } }
        [Browsable(false)] public bool CurrentAccepted { get { return selected>=0 && working[selected,35]==1; } }
        [Browsable(false)] public bool CanAccept { get { return !IsAnalysisRunning && selected>=0 && !CurrentAcquisitionReferenceOnly && !CurrentAccepted && IsReviewRowComplete(selected) && pdmap.CurrentParameterRowIndex==selected && (IsCapacitorRow(selected)||pdmap.HasSelectedReflection && Finite(pdmap.LocationM)) && ManualLocationScoreEligible; } }
        [Browsable(false)] public bool CanReportParameters { get { return selected>=0 && !CurrentAcquisitionReferenceOnly && pdmap.CurrentParameterRowIndex==selected && pdmap.ReflectionWithinWaveform; } }
        [Browsable(false)] public bool CanDelete { get { return !IsAnalysisRunning&&selected>=0&&!removed[selected]&&IsReviewRowComplete(selected)&&pdmap.CurrentParameterRowIndex==selected; } }
        [Browsable(false)] public int CurrentConfidenceSourceCode { get { return selected<0 ? -1 : confidenceSources[selected]; } }
        [Browsable(false)] public double OriginalConfidence { get { return selected<0 ? double.NaN : original[selected,33]; } }
        [Browsable(false)] public double CurrentConfidence { get { return selected<0 || CurrentAcquisitionReferenceOnly || !pdmap.HasSelectedReflection || pdmap.CurrentReflectionIndex<=pdmap.CurrentIncidentIndex ? double.NaN : working[selected,33]; } }
        public event EventHandler ResultsChanged;
        public event EventHandler CurrentPdMapChanged;

        [Category("Data")] public int PdMapId
        {
            get { return selected<0?-1:(int)working[selected,9]; }
            set { if(!SelectPdMapId(value)) throw new ArgumentException(LastError); }
        }
        [Category("Data")] public string ProjectPath
        {
            get { return projectPath; }
            set { string path=(value??"").Trim(); if(SameAnalysisProjectPath(path,projectPath))return;
                if(IsAnalysisRunning)throw new InvalidOperationException("Analysis is running; wait or clear the analysis first.");
                bool nextCapacitor=ReadCapacitorProject(path);
                ResetAnalysisArchiveContext();ResetNoiseReferences();AnalysisProjectDisplayName="";projectPath=path;capacitorProject=nextCapacitor; pdmap.ProjectPath=path; spectrum.ProjectPath=path;spectrum.CapacitorStatisticsMode=capacitorProject; LoadTreeProject();RefreshReviewUi();ResetAnalysisDirtyForProject();ScheduleSavedAnalysisPrompt();QueueDischargeIdentification(); }
        }
        [Category("Appearance"),DefaultValue(false)] public bool EnglishVersion
        {
            get { return english; }
            set { LanguageIndex=value?1:0; }
        }
        [Category("Appearance")] public int DisplayModeIndex { get { return spectrum.DisplayModeIndex; } set { spectrum.DisplayModeIndex=value; QueueDischargeIdentification(); } }
        [Browsable(false)] public bool[] PhaseVisibility { get { return spectrum.PhaseVisibility; } set { spectrum.PhaseVisibility=value;spectrum.HighlightSourceRow(selected);RefreshReviewUi(); } }
        [Browsable(false)] public double[,] ResultParameterMatrix { get { return (double[,])working.Clone(); } }
        [Browsable(false)] public double[,] OriginalParameterMatrix { get { return (double[,])original.Clone(); } }
        public double[,] GetResultParameterMatrix() { return ResultParameterMatrix; }
        public double[] GetCurrentParameterRow() { return selected<0?new double[0]:Row(working,selected); }

        /// <summary>Null retains an existing matrix. Zero result rows clears both panes. Never mutates caller arrays.</summary>
        public bool LoadDataMatrices(double[,] resultParameters,double[,] matchingWaveforms) { return LoadDataMatricesCore(resultParameters,matchingWaveforms,false); }
        public bool ForceReloadDataMatrices(double[,] resultParameters,double[,] matchingWaveforms) { return LoadDataMatricesCore(resultParameters,matchingWaveforms,true); }
        bool LoadDataMatricesCore(double[,] resultParameters,double[,] matchingWaveforms,bool force,bool[] exactPositions=null,bool[] automaticSources=null,System.Collections.Generic.Dictionary<string,ReviewAcquisitionQuality> qualities=null,bool readQualitySources=true)
        {
            bool notifyLoaded=false;
            var priorModels=pdmap.SnapshotReviewConnectingGeometry();var priorPendingModels=pendingConnectingModels;bool modelsChanged=false;
            var priorWorking=working;var priorOriginal=original;var priorMatching=matching;var priorSources=confidenceSources;
            var priorReviewed=reviewed;
            var priorRemoved=removed;
            var priorConfirmed=acceptanceConfirmed;
            var priorAutomaticSources=automaticAcceptanceRows;
            var priorExactPositions=exactManualPositionRows;
            var priorDiagnostics=algorithmDiagnostics;
            var priorQualities=acquisitionQualities;string priorQualityProject=acquisitionQualityProject;
            int priorSelected=selected,priorPreviewFilteredRow=previewFilteredRow;bool priorManualEdits=manualPositionEditsSinceLoad;string priorGeometryStatus=connectingGeometryStatus;double priorAppliedLead=appliedConnectingCableLengthM;
            try
            {
                var result=resultParameters??original; var waves=matchingWaveforms??matching;
                if(result.GetLength(1)<36)throw new ArgumentException("定位结果至少需要36列 / At least 36 columns required.");
                for(int r=0;r<result.GetLength(0);r++)
                    if(!ValidId(result[r,9]))throw new ArgumentException("第"+r+"行PDmap ID无效 / Invalid PDmap ID.");
                int count=result.GetLength(0);
                if(exactPositions!=null&&exactPositions.Length!=count)throw new ArgumentException("Manual position state must match the source row count.");
                if(automaticSources!=null&&automaticSources.Length!=count)throw new ArgumentException("Automatic acceptance source must match the source row count.");
                if(count>0 && (waves.GetLength(0)==0||waves.GetLength(1)==0))throw new ArgumentException("需要匹配波形矩阵 / Matching waveforms required.");
                if(count>0 && waves.GetLength(0)!=count && waves.GetLength(0)!=3L*count)
                    for(int r=0;r<count;r++) if(result[r,9]>=waves.GetLength(0))
                        throw new ArgumentException("匹配矩阵行数必须为N、3N或覆盖全部PDmap ID / Matching matrix row count mismatch.");
                // Compare the last INPUT, not the edited result: a LabVIEW loop must not undo user review.
                if(!force&&(resultParameters==null||Equal(original,resultParameters))&&(matchingWaveforms==null||Equal(matching,matchingWaveforms))){LastError="";return true;}
                var next=PrepareConnectingGeometry(result,result,false);var nextWaves=(double[,])waves.Clone();
                var nextQualities=ResolveAcquisitionQualities(next,qualities,readQualitySources);
                for(int r=0;r<count;r++)if(nextQualities.ContainsKey(QualityKey(next,r)))next[r,35]=0;
                NormalizeEndpointPositions(next);
                // Preserve the source matrix; legacy coincident support is not
                // ordinary confidence in the working/exported review matrix.
                for(int r=0;r<count;r++)
                    if(HVX.SignalEvidence.CoincidentConfidenceMath.IsCoincidentRow(next,r)||LabVIEWPdMap.PdMapTimingEvidence.IsNegativeDelayRow(next,r))next[r,33]=double.NaN;
                int nextRow=count==0?-1:Math.Max(0,Math.Min(selected,count-1));
                loadingData=true;synchronizing=true;
                pdmap.SetReviewConnectingGeometry(pendingConnectingModels);
                modelsChanged=true;
                working=next;matching=nextWaves;original=(double[,])result.Clone();confidenceSources=new int[count];
                acquisitionQualities=nextQualities;acquisitionQualityProject=projectPath;
                reviewed=new bool[count];
                removed=new bool[count];
                acceptanceConfirmed=new bool[count];
                automaticAcceptanceRows=automaticSources==null?new bool[count]:(bool[])automaticSources.Clone();
                for(int r=0;r<count;r++)if(IsAcquisitionReferenceOnly(r))automaticAcceptanceRows[r]=false;
                exactManualPositionRows=exactPositions==null?new bool[count]:(bool[])exactPositions.Clone();
                algorithmDiagnostics=new System.Collections.Generic.Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                var complete=BuildWaveformCompletenessMask();
                if(nextRow>=0&&!complete[nextRow])nextRow=Array.FindIndex(complete,value=>value);
                if(force)pdmap.RequireReviewInputReload();
                if(nextRow>=0 && !pdmap.LoadDataMatricesAtRow(next,nextWaves,nextRow))
                    throw new ArgumentException(pdmap.LastError);
                selected=nextRow;previewFilteredRow=-1;pendingPreviewNotification=false;
                SetConnectingGeometryLoadedStatus();
                LoadSpectrumDisplayData();spectrum.AcceptedOnly=true;
                RefreshApproximationProjection();
                lastSelectedForFilter=nextRow;
                ResetRangeFiltersForNewData();
                ResetStatisticsForNewData();
                if(selected>=0)spectrum.HighlightSourceRow(selected);
                SelectionSequence++;LastError="";RefreshReviewUi();
                notifyLoaded=true;
                return true;
            }
            catch(Exception ex)
            {
                pendingConnectingModels=priorPendingModels;
                if(modelsChanged)
                {
                    working=priorWorking;original=priorOriginal;matching=priorMatching;confidenceSources=priorSources;selected=priorSelected;previewFilteredRow=priorPreviewFilteredRow;
                    reviewed=priorReviewed;
                    removed=priorRemoved;
                    acceptanceConfirmed=priorConfirmed;
                    automaticAcceptanceRows=priorAutomaticSources;
                    exactManualPositionRows=priorExactPositions;
                    algorithmDiagnostics=priorDiagnostics;
                    acquisitionQualities=priorQualities;acquisitionQualityProject=priorQualityProject;
                    manualPositionEditsSinceLoad=priorManualEdits;connectingGeometryStatus=priorGeometryStatus;appliedConnectingCableLengthM=priorAppliedLead;
                    pdmap.SetReviewConnectingGeometry(priorModels);
                    pdmap.RequireReviewInputReload();
                    if(priorSelected>=0)
                    {pdmap.BeginConnectingGeometryReload();try {pdmap.LoadDataMatricesAtRow(priorWorking,priorMatching,priorSelected);}finally{pdmap.EndConnectingGeometryReload();}}
                    LoadSpectrumDisplayData();spectrum.AcceptedOnly=true;
                }
                LastError=ex.Message;RefreshReviewUi();return false;
            }
            finally
            {
                synchronizing=false;loadingData=false;
                // Only publish the final post-filter selection, never the temporary initial row.
                if(notifyLoaded){automaticEvaluationDataProject=projectPath;SyncAnalysisTree(true);MarkAnalysisDirty();NotifyReviewHost(CurrentPdMapChanged);QueueAutomaticEvaluation();}
            }
        }
        public bool SelectSourceRow(int row)
        { return SelectSourceRowCore(row,false); }
        bool SelectSourceRowCore(int row,bool markReviewed)
        {
            if(row<0||row>=SourceRowCount){LastError=L("Source row is out of range.","源行超出范围。");RefreshReviewUi();return false;}
            // Optional statistics filters limit plotted results and sequential
            // navigation, not an explicit request to inspect a source waveform.
            // Keep the mandatory acquisition-completeness check independent.
            if(!IsReviewRowComplete(row)){LastError=L("This waveform does not meet the acquisition completeness requirement.","该波形不满足采集完整性要求。");RefreshReviewUi();return false;}
            if(row==selected){previewFilteredRow=IsReviewRowVisible(row)?-1:row;LastError="";pdmap.ClearConnectingCableUiFeedback();ShowPdMapWorkspace();spectrum.HighlightSourceRow(row);RefreshReviewUi();SyncTreeSelection();return true;}
            pdmap.CompleteReviewPreview();pdmap.FlushReviewPreview();spectrum.FlushReviewPreview();
            bool wasSynchronizing=synchronizing;synchronizing=true;
            try
            {
                // An all-incomplete load deliberately selects no child waveform.
                // If geometry/filter changes later make a row eligible, bind the
                // current source matrices before selecting it, never a prior batch.
                bool loaded=selected<0?pdmap.LoadDataMatricesAtRow(working,matching,row):pdmap.SelectSourceRow(row);
                if(!loaded){LastError=pdmap.LastError;return false;}
                pdmap.ClearConnectingCableUiFeedback();
                selected=row;previewFilteredRow=IsReviewRowVisible(row)?-1:row;lastSelectedForFilter=row;ShowPdMapWorkspace();RefreshApproximationProjection();
                spectrum.HighlightSourceRow(row);if(!loadingData)SelectionSequence++;LastError="";RefreshReviewUi();
            }
            finally { synchronizing=wasSynchronizing; }
            SyncTreeSelection();if(!loadingData)NotifyReviewHost(CurrentPdMapChanged);return true;
        }
        public bool SelectPdMapId(int id)
        {
            // IDs repeat across records/phases. Prefer the current record, then require a unique match.
            var rows=Enumerable.Range(0,SourceRowCount).Where(r=>working[r,9]==id&&IsReviewRowComplete(r)).ToArray();
            if(selected>=0)
            {
                var local=rows.Where(r=>working[r,0]==working[selected,0]&&working[r,1]==working[selected,1]&&working[r,8]==working[selected,8]).ToArray();
                if(local.Length==1)return SelectSourceRow(local[0]);
            }
            if(rows.Length==1)return SelectSourceRow(rows[0]);
            LastError=rows.Length==0?(L("PDmap ID not found.","未找到PDmap ID。")):(L("Duplicate ID: select SourceRowIndex.","PDmap ID重复，请按源行选择。"));
            RefreshReviewUi();return false;
        }
        int AdjacentRow(int direction)
        {
            int previous,next;GetAdjacentRows(out previous,out next);return direction<0?previous:next;
        }
        void GetAdjacentRows(out int previous,out int next)
        {
            previous=next=-1;if(selected<0)return;
            double id=working[selected,9];
            for(int r=0;r<SourceRowCount;r++)
            {
                if(r==selected||!IsReviewRowVisible(r)||working[r,0]!=working[selected,0]||
                    working[r,1]!=working[selected,1]||working[r,8]!=working[selected,8])continue;
                double candidate=working[r,9];
                if(candidate<id||candidate==id&&r<selected)
                { if(previous<0||candidate>working[previous,9]||candidate==working[previous,9]&&r>previous)previous=r; }
                else if(next<0||candidate<working[next,9]||candidate==working[next,9]&&r<next)next=r;
            }
            // Continue across LOC records when reviewing an integrated project.
            if(!string.IsNullOrEmpty(projectPath)&&analysisTree!=null&&analysisTree.GetDataPaths().Length>1){
                if(previous<0)for(int r=selected-1;r>=0;r--)if(IsReviewRowVisible(r)){previous=r;break;}
                if(next<0)for(int r=selected+1;r<SourceRowCount;r++)if(IsReviewRowVisible(r)){next=r;break;}
            }
        }
        public bool NextPdMap() { int row=AdjacentRow(1);return row>=0&&ViewSourceRow(row); }
        public bool PreviousPdMap() { int row=AdjacentRow(-1);return row>=0&&ViewSourceRow(row); }
        public bool AcceptCurrent() { return SetAccepted(true); }
        bool AcceptAndAdvanceManual()
        {
            if(!AcceptCurrent())return false;
            if(!manualReviewAutoAdvance)return true;
            int next=AdjacentUnacceptedRow();
            if(next>=0)SelectSourceRowCore(next,false);
            return true;
        }
        int AdjacentUnacceptedRow()
        {
            if(selected<0)return -1;
            double phase=working[selected,0],dataId=working[selected,1],record=working[selected,8],id=working[selected,9];
            int next=-1;
            for(int r=0;r<SourceRowCount;r++)
            {
                if(r==selected||!IsReviewRowComplete(r)||!IsReviewRowVisible(r)||working[r,35]==1||
                   working[r,0]!=phase||working[r,1]!=dataId||working[r,8]!=record)continue;
                double candidate=working[r,9];
                if(candidate>id||candidate==id&&r>selected)
                    if(next<0||candidate<working[next,9]||candidate==working[next,9]&&r<next)next=r;
            }
            if(next>=0)return next;
            for(int r=selected+1;r<SourceRowCount;r++)
                if(IsReviewRowComplete(r)&&IsReviewRowVisible(r)&&working[r,35]!=1)return r;
            return -1;
        }
        public bool DeleteCurrent() { return SetAccepted(false); }
        bool SetAccepted(bool accepted)
        {
            if(IsAnalysisRunning||selected<0||accepted&&CurrentAccepted||!accepted&&!CanDelete)return false;
            if(accepted&&CurrentAcquisitionReferenceOnly){LastError=AcquisitionRiskText();RefreshReviewUi();return false;}
            if(accepted&&!CanAccept)
            {LastError=pdmap.HasSelectedReflection&&!ManualLocationScoreEligible?ManualLocationScoreReason():L("Select a valid reflection before accepting this location.","请先选定有效反射波，再接受定位结果。");RefreshReviewUi();return false;}
            // Accept the candidate actually shown above, including endpoint/joint
            // approximation. Merely flipping the old input flag can leave its
            // negative/out-of-cable position invisible in Q-L.
            if(accepted&&pdmap.CurrentParameterRowIndex!=selected)
            { LastError=L("The displayed waveform does not match the selected source row.","当前波形与选中源行不一致，请重新选择。");RefreshReviewUi();return false; }
            var row=accepted&&!IsCapacitorRow(selected)?pdmap.GetReviewParameterRow():Row(working,selected);
            if(row.Length!=working.GetLength(1))return false;
            row[35]=accepted?1:0;
            var learningSample=accepted?CaptureAcceptedLocationLearningSample(selected,row):null;
            removed[selected]=!accepted;reviewed[selected]=true;
            if(accepted)acceptanceConfirmed[selected]=true;
            int priorResultSequence=ResultChangeSequence;
            CommitRow(selected,row);
            SyncReviewedTree();MarkAnalysisDirty();
            RefreshReviewUi();
            if(ResultChangeSequence==priorResultSequence){ResultChangeSequence++;NotifyReviewHost(ResultsChanged);}
            if(learningSample!=null)QueueAcceptedLocationLearningSample(learningSample);
            return true;
        }
        void OnAnalysisEdited(object sender,PdMapAnalysisChangedEventArgs e)
        {
            if(synchronizing||e.SourceRowIndex!=selected||selected<0)return;
            if(IsCapacitorRow(selected)){pdmap.CommitReviewRow(selected,Row(working,selected),false);return;}
            // Editing is an explicit request to inspect this waveform. Its new
            // confidence/location may leave the filtered statistics set, but
            // must not cause the mouse gesture to load a different PDmap.
            previewFilteredRow=selected;
            confidenceSources[selected]=(int)e.ConfidenceSourceCode;
            CommitRow(selected,e.ParameterRow,e.IsPreview);
            if(!e.IsPreview)RecordOperation("WaveformEdit",e.ChangeReason+"; "+OperationContext()+"; T1="+pdmap.CurrentIncidentIndex+"; T2="+pdmap.CurrentReflectionIndex+"; correlation="+pdmap.CurrentCorrelationPercent+"; confidence="+pdmap.CurrentConfidencePercent+"; status="+pdmap.ConfidenceStatusCode+"; "+pdmap.ConfidenceDetails);
        }
        void CommitRow(int index,double[] row,bool preview=false)
        {
            if(index<0||index>=SourceRowCount||row==null||row.Length!=working.GetLength(1))return;
            row=(double[])row.Clone();
            if(IsAcquisitionReferenceOnly(index))row[35]=0;
            row[13]=HVX.SignalEvidence.BoundaryConfidenceMath.ProjectEndpointPosition(row[13],row[5]);
            bool changed=false;
            if(!rebuildingConnectingGeometry)
                foreach(int column in new[]{10,11,12,13})if(!working[index,column].Equals(row[column])){manualPositionEditsSinceLoad=true;break;}
            for(int c=0;c<row.Length;c++)if(!working[index,c].Equals(row[c])){working[index,c]=row[c];changed=true;}
            if(changed)
            {
                // Mark scores stale even for a cheap drag preview. Rebuild only
                // on commit or an explicit filter request, never per paint frame.
                InvalidateLocationScores();
                pdmap.CommitReviewRow(index,row,preview);
                spectrum.UpdateReviewRow(index,SpectrumDisplayRow(index,row),preview);spectrum.HighlightSourceRow(selected);
                if(!preview)RefreshStatisticsAfterEdit();
                ResultChangeSequence++;
                if(preview)pendingPreviewNotification=true;
                MarkAnalysisDirty();
            }
            if(!changed&&!preview)
            {
                pdmap.FlushReviewPreview();spectrum.FlushReviewPreview();
                spectrum.HighlightSourceRow(selected);RefreshStatisticsAfterEdit();
            }
            RefreshReviewUi();
            // Paint the completed lower-pane state before a synchronous LabVIEW
            // ResultsChanged handler can start longer host-side work. Update only
            // this control; never pump unrelated input with Application.DoEvents.
            bool publish=!preview&&(changed||pendingPreviewNotification);
            if(publish)
            {
                pendingPreviewNotification=false;
                if(spectrum.IsHandleCreated&&!spectrum.IsDisposed)spectrum.Update();
                NotifyReviewHost(ResultsChanged);
            }
        }
        [Browsable(false)] public int HostEventErrorCount { get; private set; }
        void NotifyReviewHost(EventHandler handlers)
        {
            PublishReviewNotification(handlers);
        }
        int lastSelectedForFilter=-1;
        bool IsReviewRowVisible(int row)
        {
            // This is the filtered statistics/navigation set. Explicit waveform
            // inspection is separately gated by physical acquisition completeness.
            return row>=0&&row<SourceRowCount&&(reviewMask==null||reviewMask[row]);
        }
        bool HasExplicitFilteredPreview()
        {return selected>=0&&selected==previewFilteredRow&&IsReviewRowComplete(selected);}
        void EnsureVisibleSelection()
        {
            if(selected>=0&&(IsReviewRowVisible(selected)||HasExplicitFilteredPreview())){spectrum.HighlightSourceRow(selected);RefreshReviewUi();return;}
            int prior=selected>=0?selected:lastSelectedForFilter;
            var candidates=Enumerable.Range(0,SourceRowCount).Where(IsReviewRowVisible).ToArray();
            if(candidates.Length==0)
            {
                bool changed=selected>=0;
                if(changed)lastSelectedForFilter=selected;
                selected=-1;previewFilteredRow=-1;spectrum.HighlightSourceRow(-1);
                if(changed&&!loadingData)SelectionSequence++;
                RefreshReviewUi();
                if(changed&&!loadingData)NotifyReviewHost(CurrentPdMapChanged);
                return;
            }
            if(prior>=0&&prior<SourceRowCount)
            {
                var local=candidates.Where(r=>working[r,0]==working[prior,0]&&working[r,1]==working[prior,1]&&working[r,8]==working[prior,8]).ToArray();
                if(local.Length>0)candidates=local;
                candidates=candidates.OrderBy(r=>Math.Abs(working[r,9]-working[prior,9])).ThenBy(r=>working[r,9]).ThenBy(r=>r).ToArray();
            }
            SelectSourceRowCore(candidates[0],false);
        }
        void OnSpectrumRegionAction(object sender,EventArgs e)
        {
            if(spectrum.RegionActionCommand!=1)return;
            // Review delete is reversible col35=0, never destruction of a source row.
            var rows=spectrum.LastSelectedSourceRowIndices.Where(r=>r>=0&&r<SourceRowCount).Distinct().ToArray();
            int changed=0;
            foreach(int row in rows)
            {
                removed[row]=true;reviewed[row]=true;
                if(working[row,35]==0)continue;
                working[row,35]=0;changed++;
                // Keep the child cache in sync; rebuild its derived overlay only once.
                pdmap.CommitReviewRow(row,Row(working,row),true);
            }
            pdmap.FlushReviewPreview();spectrum.CancelReviewAcceptance(rows);
            spectrum.HighlightSourceRow(selected);RefreshStatisticsAfterEdit();
            if(rows.Length>0){SyncReviewedTree();MarkAnalysisDirty();}
            ResultChangeSequence+=changed;RefreshReviewUi();
            if(changed>0)
            {
                pendingPreviewNotification=false;
                if(spectrum.IsHandleCreated&&!spectrum.IsDisposed)spectrum.Update();
                NotifyReviewHost(ResultsChanged);
            }
        }
        static bool ValidId(double v) { return !double.IsNaN(v)&&!double.IsInfinity(v)&&v>=0&&v<=int.MaxValue&&v==Math.Truncate(v); }
        static void NormalizeEndpointPositions(double[,] matrix)
        {
            if(matrix==null||matrix.GetLength(1)<14)return;
            for(int r=0;r<matrix.GetLength(0);r++)
                matrix[r,13]=HVX.SignalEvidence.BoundaryConfidenceMath.ProjectEndpointPosition(matrix[r,13],matrix[r,5]);
        }
        static bool Finite(double v) { return !double.IsNaN(v)&&!double.IsInfinity(v); }
        static double[] Row(double[,] a,int r) { var v=new double[a.GetLength(1)];for(int c=0;c<v.Length;c++)v[c]=a[r,c];return v; }
        static bool Equal(double[,] a,double[,] b) { if(ReferenceEquals(a,b))return true;if(a.GetLength(0)!=b.GetLength(0)||a.GetLength(1)!=b.GetLength(1))return false;for(int r=0;r<a.GetLength(0);r++)for(int c=0;c<a.GetLength(1);c++)if(!a[r,c].Equals(b[r,c]))return false;return true; }
    }
}
