using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using LabVIEWChartCommon;

namespace LabVIEWPdMap
{
    public enum ReflectionMoveResult { Moved, AlreadyAtTarget, UnsupportedMode, NoWaveform, SelectionUnavailable }

    [ToolboxItem(true)]
    public sealed partial class PdMapControl : UserControl
    {
        double[] pulse=Array.Empty<double>(), calibration=Array.Empty<double>(), attenuation=Array.Empty<double>();
        double[] alignedCalibration=Array.Empty<double>(), alignedAttenuation=Array.Empty<double>();
        double[] filteredPulse=Array.Empty<double>(), filteredCalibration=Array.Empty<double>(), filteredAttenuation=Array.Empty<double>();
        double[] originalPulse=Array.Empty<double>(), originalParameterRow=Array.Empty<double>();
        double[] positions=Array.Empty<double>(), charges=Array.Empty<double>(), overlayPhases=Array.Empty<double>(), connectors=Array.Empty<double>();
        double[,] positioningMatrix, matchingMatrix, previousPositioningMatrix;
        bool[] curveVisibility={true,false,false,false,false,false,true,true};
        double[] filterParameters={0,50000,7000000,4,5,2};
        int incident, reflection, initialIncident, initialReflection, calibrationPeak, n1=18, n2=28;
        bool reflectionSelected=true, initialReflectionSelected=true, originalReflectionSelected=true;
        int currentParameterRowIndex=-1, currentPdMapId=-1, samePositionPulseCount, boundaryDirection=1;
        int originalIncident, originalReflection, originalSamePositionPulseCount, originalBoundaryDirection=1;
        double dtNs=1, velocityMPerUs=170, cableLengthM=415, rawCharge, compensatedCharge, correlation, confidence, voltageKv, phaseDeg;
        double originalDtNs, originalVelocity, originalCableLength, originalRawCharge, originalCompensatedCharge, originalCorrelation, originalConfidence, originalVoltageKv, originalPhaseDeg, originalLocationM, originalTimeDifferenceNs;
        bool originalVelocityConfigured;
        double acceptedPositionErrorPercent=1.0, currentLocationM, currentTimeDifferenceNs;
        bool english, showCalibration=true, showAttenuation=true, showOverlay, mirrorLocation, matrixDriven, showTitle, restoreCommandState;
        double calibrationOpacity=.88, attenuationOpacity=.52;
        Color pulseColor=Color.FromArgb(44,133,110), calibrationColor=Color.FromArgb(230,74,55), attenuationColor=Color.FromArgb(45,132,193);
        Color incidentColor=Color.FromArgb(0,145,202), reflectionColor=Color.FromArgb(239,117,0), pointColor=Color.FromArgb(222,55,45), connectorColor=Color.FromArgb(35,157,168);
        Color filteredPulseColor=Color.FromArgb(44,133,110), filteredCalibrationColor=Color.FromArgb(220,38,38), filteredAttenuationColor=Color.FromArgb(124,58,237), boundaryColor=Color.FromArgb(82,91,104);
        double viewX0, viewX1, viewY0, viewY1; bool viewReady;
        Rectangle lastPlot;
        readonly ContextMenuStrip menu;
        readonly ExportPopupButton exportButton;
        bool useInternalContextMenu=true;

        public PdMapControl()
        {
            DoubleBuffered=true; MinimumSize=new Size(360,250); BackColor=HVX.PdReview.ReviewTheme.Surface; ForeColor=HVX.PdReview.ReviewTheme.Ink;var ownedFont=HVX.PdReview.ReviewTheme.RegularFont();Font=ownedFont;Disposed+=(s,e)=>ownedFont.Dispose();
            SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.UserPaint,true);
            TabStop=true;
            Width=1100; Height=650;
            menu=new ContextMenuStrip();
            menu.Items.Add("还原当前 MAP",null,(s,e)=>RestoreOriginalMapNow());
            menu.Items.Add("还原入射波",null,(s,e)=>ResetIncidentToInitial());
            menu.Items.Add("还原反射波",null,(s,e)=>ResetReflectionToInitial());
            ContextMenuStrip=useInternalContextMenu?menu:null;
            exportButton=ExportPopupButton.Attach(this,()=>english,ExportDataDialog,ExportImageDialog);
            InitializeCursorInteraction();
            InitializeCursorStepButtons();
            InitializeDisplayOptions();
        }

        [Category("Interaction")] public bool UseInternalContextMenu{get{return useInternalContextMenu;}set{useInternalContextMenu=value;ContextMenuStrip=value?menu:null;}} public void SetInternalContextMenuEnabled(bool enabled){UseInternalContextMenu=enabled;}
        [Category("Appearance"),DefaultValue(false)] public bool ShowTitle{get{return showTitle;}set{showTitle=value;Invalidate();}}

        [Browsable(false)] public int CurrentIncidentIndex=>incident;
        [Browsable(false)] public bool HasSelectedReflection { get { ValidateReflectionSelectionWindow();return reflectionSelected; } }
        [Browsable(false)] public bool ReflectionWithinWaveform=>reflectionSelected&&reflection>=0&&reflection<pulse.Length;
        [Browsable(false)] public int CurrentReflectionIndex { get { ValidateReflectionSelectionWindow();return reflectionSelected?reflection:-1; } }
        [Browsable(false)] public double TimeDifferenceNs=>!reflectionSelected?double.NaN:matrixDriven?currentTimeDifferenceNs:Math.Abs(reflection-(double)incident)*dtNs;
        [Browsable(false)] public double SignedTimeDifferenceNs=>reflectionSelected?(reflection-(double)incident)*dtNs:double.NaN;
        [Browsable(false)] public double PropagationDistanceM=>TimeDifferenceNs*velocityMPerUs/2000.0;
        [Browsable(false)] public double LocationM=>!reflectionSelected?double.NaN:matrixDriven?currentLocationM:CalculatedLocation();
        [Browsable(false)] public bool LocationOutOfRange=>IsOutsideCableDisplayRange;
        [Browsable(false)] public string LastOutputPath{get;private set;}="";
        [Browsable(false)] public string LastError{get;private set;}="";
        [Browsable(false)] public string FilterStatus{get;private set;}="";
        [Browsable(false)] public int CurrentPdMapId=>currentPdMapId;
        [Browsable(false)] public int CurrentParameterRowIndex=>currentParameterRowIndex;
        [Browsable(false)] public double CurrentVoltageKv=>voltageKv;
        [Browsable(false)] public double CurrentPhaseDeg=>phaseDeg;
        [Browsable(false)] public double CurrentRawChargePc=>rawCharge;
        [Browsable(false)] public double CurrentCompensatedChargePc=>compensatedCharge;
        [Browsable(false)] public double CurrentCorrelationPercent=>correlation;
        [Browsable(false)] public double CurrentConfidencePercent=>confidence;
        [Browsable(false)] public int SameLocationPulseCount=>samePositionPulseCount;
        [Browsable(false)] public int RestoreSequence{get;private set;}
        [Category("LabVIEW Commands"),DefaultValue(false)]
        public bool RestoreOriginalMapPositions
        {
            get{return restoreCommandState;}
            set{ExecuteRestoreCommand(value);}
        }
        [Browsable(false),DefaultValue(false)]
        public bool RestoreOriginalMap
        {
            get{return restoreCommandState;}
            set{ExecuteRestoreCommand(value);}
        }

        [Category("Language"),DefaultValue(false),Description("是否英文 / English display. False: 中文; True: English.")]
        public bool EnglishVersion { get { return english; } set { SetLanguage(value); } }
        public void SetEnglishDisplay(bool englishVersion){SetLanguage(englishVersion);}
        public void SetLanguage(bool englishVersion){if(english==englishVersion)return;LanguageIndex=englishVersion?1:0;}
        public void SetLanguageCode(string code){SetLanguage(!string.IsNullOrWhiteSpace(code)&&code.StartsWith("en",StringComparison.OrdinalIgnoreCase));}
        void UpdateMenu(){string[] z=L(new[]{"Restore current MAP","Restore incident","Restore reflection"},new[]{"还原当前 MAP","还原入射波","还原反射波"});for(int i=0;i<Math.Min(menu.Items.Count,z.Length);i++)menu.Items[i].Text=z[i];}
        public void SetWaveform(double[] pulseWaveform,double sampleIntervalNs)
        {
            double[] next=Sanitize(pulseWaveform);double nextDt=sampleIntervalNs>0?sampleIntervalNs:1;
            if(!forceReloadNextInput&&!matrixDriven&&SameValues(pulse,next)&&dtNs.Equals(nextDt))return;
            forceReloadNextInput=false;ClearCursorSelection();ClearPositionApproximation();matrixDriven=false;pulse=next;dtNs=nextDt;
            reflectionSelected=initialReflectionSelected=true;
            velocityConfigured=manualVelocityConfigured;
            if(manualVelocityConfigured)velocityMPerUs=manualVelocityMPerUs;
            SelectProjectCalibrationForMap(null);
            ClampIndices();AlignCalibration();RecomputeFilteredCurves();ResetView();Invalidate();
        }
        public void SetWaveformBySamplingRate(double[] pulseWaveform,double samplingRateHz){SetWaveform(pulseWaveform,samplingRateHz>0?1e9/samplingRateHz:1);}
        public void SetInitialIndices(int incidentIndex,int reflectionIndex)
        {
            bool reflectionExists=reflectionIndex>=0&&reflectionIndex<pulse.Length;
            int a=(int)Clamp(incidentIndex,0,Math.Max(0,pulse.Length-1)),b=reflectionExists?reflectionIndex:a;
            if(initialIncident==a&&initialReflection==b&&reflectionSelected)return;
            ClearCursorSelection();ClearPositionApproximation();initialIncident=incident=a;initialReflection=reflection=b;
            reflectionSelected=initialReflectionSelected=reflectionExists;
            ClampIndices();ValidateReflectionSelectionWindow();AlignCalibration();RecomputeFilteredCurves();Invalidate();
        }
        public void SetSelectionWindow(int pointsBefore,int pointsAfter){n1=Math.Max(0,pointsBefore);n2=Math.Max(0,pointsAfter);Invalidate();}
        public void SetCableParameters(double waveVelocityMPerUs,double cableLengthMeters)
        {
            bool hadGeometry=velocityConfigured||MixedCableCalculation;
            double speed=IsFinite(waveVelocityMPerUs)&&waveVelocityMPerUs>0?NormalizeVelocity(waveVelocityMPerUs):double.NaN;
            double length=IsFinite(cableLengthMeters)&&cableLengthMeters>0?cableLengthMeters:cableLengthM;
            bool changed=length!=cableLengthM;
            if(IsFinite(speed)&&speed>0){changed|=!manualVelocityConfigured||velocityMPerUs!=speed;velocityMPerUs=manualVelocityMPerUs=speed;velocityConfigured=manualVelocityConfigured=true;}
            if(!changed)return;
            cableLengthM=length;
            if(!matrixDriven&&!hadGeometry&&initialReflectionSelected){reflection=initialReflection;reflectionSelected=true;}
            UpdateCurrentMaterialSegments();RefreshCableStructureAnalysis();
        }
        public void SetMirrorLocation(bool mirror){if(mirrorLocation==mirror)return;mirrorLocation=mirror;RefreshCableStructureAnalysis();}
        public void SetAnalysisValues(double compensatedChargePc,double correlationPercent,double reflectionConfidencePercent){compensatedCharge=compensatedChargePc;correlation=correlationPercent;confidence=reflectionConfidencePercent;ValidateReflectionSelectionWindow();NormalizeLoadedCoincidentConfidence();if(matrixDriven&&!reflectionSelected)SetUnselectedReflectionAnalysis();Invalidate();}
        public void SetCalibration(double[] waveform,double[] decayCurve,int peakIndex){calibration=Sanitize(waveform);attenuation=Sanitize(decayCurve);calibrationPeak=peakIndex;CaptureLegacyCalibration();SelectProjectCalibrationForMap(matrixDriven?originalParameterRow:null);AlignCalibration();RecomputeFilteredCurves();viewReady=false;Invalidate();}
        public void SetCalibrationDisplay(bool calibrationVisible,bool attenuationVisible,double calibrationAlpha,double attenuationAlpha){showCalibration=calibrationVisible;showAttenuation=attenuationVisible;calibrationOpacity=Clamp(calibrationAlpha,0,1);attenuationOpacity=Clamp(attenuationAlpha,0,1);viewReady=false;Invalidate();}
        public void SetLocationOverlay(double[] locationMeters,double[] dischargePc,bool visible)
        {
            positions=locationMeters==null?Array.Empty<double>():(double[])locationMeters.Clone();charges=dischargePc==null?Array.Empty<double>():(double[])dischargePc.Clone();
            overlayPhases=new double[Math.Min(positions.Length,charges.Length)];standaloneOverlay=true;previousOverlayExplicit=false;previousPositioningMatrix=null;
            SetOverlayClassificationSource(null,null);showPreviousPositioningResults=showOverlay=visible;ResetView();Invalidate();
        }
        public void SetConnectors(double[] connectorPositionsMeters){SetExplicitConnectorPositions(connectorPositionsMeters);Invalidate();}
        public void SetColors(int[] argbColors)
        {
            if(argbColors==null)return; Color[] d={pulseColor,calibrationColor,attenuationColor,incidentColor,reflectionColor,pointColor,connectorColor,filteredPulseColor,filteredCalibrationColor,filteredAttenuationColor};
            for(int i=0;i<Math.Min(d.Length,argbColors.Length);i++)d[i]=Color.FromArgb(argbColors[i]);
            pulseColor=d[0];calibrationColor=d[1];attenuationColor=d[2];incidentColor=d[3];reflectionColor=d[4];pointColor=d[5];connectorColor=d[6];filteredPulseColor=d[7];filteredCalibrationColor=d[8];filteredAttenuationColor=d[9];Invalidate();
        }

        /// <summary>
        /// V1.11.0 matrix-driven LabVIEW entry point.  Ports are ordered exactly as documented:
        /// positioning matrix, matching-map matrix, PDmap ID, 8 visibility flags,
        /// 6 filter values, previous positioning matrix, joint positions, accepted error %, language.
        /// All array/matrix inputs may be null; the last valid value is retained.
        /// </summary>
        void SetPdMapDataCore(double[,] positioningResult,double[,] pdmapMatchingMatrix,double pdmapId,bool[] visibility,double[] filters,double[,] previousPositioningResults,double[] connectorPositionsM,double acceptedErrorPercent,bool englishVersion)
        {
            pendingPdMapId=null;
            SetLanguage(englishVersion);
            LastError="";
            double[] previousFilters=filterParameters;
            double previousTolerance=acceptedPositionErrorPercent;
            if(visibility!=null){curveVisibility=NormalizeVisibility(visibility);UpdateDisplayOptions();}
            if(filters!=null)filterParameters=NormalizeFilters(filters);
            if(connectorPositionsM!=null)SetExplicitConnectorPositions(connectorPositionsM);
            if(IsFinite(acceptedErrorPercent)&&acceptedErrorPercent>=0)acceptedPositionErrorPercent=acceptedErrorPercent;
            bool parametersChanged=IsUsableMatrix(positioningResult,36)&&UpdateMatrixSnapshot(ref positioningMatrix,positioningResult);
            bool matchingChanged=IsUsableMatrix(pdmapMatchingMatrix,1)&&UpdateMatrixSnapshot(ref matchingMatrix,pdmapMatchingMatrix);
            if(previousPositioningResults!=null){previousOverlayExplicit=true;UpdateMatrixSnapshot(ref previousPositioningMatrix,previousPositioningResults.GetLength(0)>0?previousPositioningResults:null);}
            if(positioningResult!=null||previousPositioningResults!=null)standaloneOverlay=false;
            BuildPreviousOverlay();
            if(!IsUsableMatrix(positioningMatrix,36)||!IsUsableMatrix(matchingMatrix,1))
            {
                if(parametersChanged||matchingChanged)forceReloadNextInput=true;
                LastError=L("Positioning-result or matching-map matrix is empty.","定位结果参数矩阵或匹配图矩阵为空。");
                Invalidate();return;
            }
            int requested=IsFinite(pdmapId)?(int)Math.Round(pdmapId):currentPdMapId;
            if(!forceReloadNextInput&&matrixDriven&&requested==currentPdMapId&&(reviewSourceRowOverride<0||reviewSourceRowOverride==currentParameterRowIndex)&&!parametersChanged&&!matchingChanged&&originalParameterRow.Length>0
                &&loadedRow==currentParameterRowIndex&&ReferenceEquals(loadedPulse,pulse)&&ReferenceEquals(loadedCalibration,calibration)
                &&ReferenceEquals(loadedAttenuation,attenuation)&&ReferenceEquals(loadedParameters,originalParameterRow))
            {
                // Matrices are compared by value: in-place edits and a newly marshalled
                // array are both supported. Avoid extracting/filtering the same row again.
                if(!SameValues(previousFilters,filterParameters))RecomputeFilteredCurves();
                if(previousTolerance!=acceptedPositionErrorPercent)RefreshPositionApproximationForTargets();
                CountSamePositionPulses(DisplayedLocationM);Invalidate();return;
            }
            int row=FindParameterRow(positioningMatrix,requested);
            if(row<0)
            {
                if(parametersChanged||matchingChanged)forceReloadNextInput=true;
                LastError=(L("PDmap ID was not found: ","未找到 PDmap ID："))+requested.ToString(CultureInfo.InvariantCulture);
                Invalidate();return;
            }
            int waveformRow=ResolveMatchingRow(row,requested);
            if(waveformRow<0)
            {
                if(parametersChanged||matchingChanged)forceReloadNextInput=true;
                LastError=L("No matching waveform row corresponds to the selected PDmap ID.","当前 PDmap ID 没有对应的匹配图波形行。");
                Invalidate();return;
            }

            double[] parameterRow=ExtractMatrixRow(positioningMatrix,row,positioningMatrix.GetLength(1));
            int expected=ExpectedWaveformLength(parameterRow,matchingMatrix.GetLength(1));
            double[] nextPulse=ExtractFiniteWaveform(matchingMatrix,waveformRow,expected);
            if(nextPulse.Length==0)
            {
                if(parametersChanged||matchingChanged)forceReloadNextInput=true;
                LastError=L("The selected matching waveform is empty.","选中的匹配图波形为空。");
                Invalidate();return;
            }
            int baseRows=positioningMatrix.GetLength(0);
            double[] nextCalibration=ProjectCalibrationEnabled?legacyCalibration:calibration,nextAttenuation=ProjectCalibrationEnabled?legacyAttenuation:attenuation;
            if(matchingMatrix.GetLength(0)==baseRows*3)
            {
                nextCalibration=ExtractFiniteWaveform(matchingMatrix,waveformRow+baseRows,expected);
                nextAttenuation=ExtractFiniteWaveform(matchingMatrix,waveformRow+baseRows*2,expected);
            }
            else if(pdmapMatchingMatrix!=null||requested!=currentPdMapId)
            {
                nextCalibration=Array.Empty<double>();nextAttenuation=Array.Empty<double>();
            }
            // LabVIEW commonly writes the same inputs every loop iteration. Such refreshes
            // must not cancel pointer capture, overwrite manual cursor positions, or zoom out.
            bool sameInput=!forceReloadNextInput&&matrixDriven&&currentPdMapId==requested&&currentParameterRowIndex==row
                &&SameValues(originalParameterRow,parameterRow)&&SameValues(pulse,nextPulse)
                &&SameValues(ProjectCalibrationEnabled?legacyCalibration:calibration,nextCalibration)&&SameValues(ProjectCalibrationEnabled?legacyAttenuation:attenuation,nextAttenuation);
            currentParameterRowIndex=row;
            if(sameInput)
            {
                if(!SameValues(previousFilters,filterParameters))RecomputeFilteredCurves();
                if(previousTolerance!=acceptedPositionErrorPercent)RefreshPositionApproximationForTargets();
                CountSamePositionPulses(DisplayedLocationM);RememberLoadedInputs();Invalidate();return;
            }
            forceReloadNextInput=false;ClearCursorSelection();ClearPositionApproximation();currentPdMapId=requested;
            pulse=nextPulse;calibration=nextCalibration;attenuation=nextAttenuation;
            calibrationPeak=IndexOfAbsolutePeak(calibration);
            CaptureLegacyCalibration();
            showCalibration=true;showAttenuation=true;
            ApplyParameterRow(parameterRow,true);
            matrixDriven=true;
            SelectProjectCalibrationForMap(parameterRow);
            ClampIndices();AlignCalibration();RecomputeFilteredCurves();
            CountSamePositionPulses(currentLocationM);
            CaptureOriginalSnapshot(parameterRow);
            if(ConnectingCableLengthM>0||connectingGeometryReload)RefreshConnectingPositionOnly();else if(MixedCableCalculation)UpdateDynamicAnalysis();
            ValidateReflectionSelectionWindow();
            if(!connectingGeometryReload)CommitPositionApproximation();
            RememberLoadedInputs();
            ResetView();Invalidate();
        }

        public void LoadPdMapData(double[,] positioningResult,double[,] pdmapMatchingMatrix,double pdmapId,bool[] visibility,double[] filters,double[,] previousPositioningResults,double[] connectorPositionsM,double acceptedErrorPercent,bool englishVersion)
        {
            SetPdMapData(positioningResult,pdmapMatchingMatrix,pdmapId,visibility,filters,previousPositioningResults,connectorPositionsM,acceptedErrorPercent,englishVersion);
        }

        /// <summary>Alternate nine-port form whose seventh port is the one-shot restore command.</summary>
        public void SetPdMapAnalysisData(double[,] positioningResult,double[,] pdmapMatchingMatrix,int pdmapId,bool[] visibility,double[] filters,double[,] previousPositioningResults,bool restoreOriginalMap,double acceptedErrorPercent,bool englishVersion)
        {
            SetPdMapData(positioningResult,pdmapMatchingMatrix,pdmapId,visibility,filters,previousPositioningResults,null,acceptedErrorPercent,englishVersion);
            ExecuteRestoreCommand(restoreOriginalMap);
        }

        public void SetCurveVisibility(bool[] visibility){if(visibility!=null){curveVisibility=NormalizeVisibility(visibility);UpdateDisplayOptions();viewReady=false;Invalidate();}}
        public void SetFilterParameters(double[] filters){if(filters!=null){filterParameters=NormalizeFilters(filters);RecomputeFilteredCurves();FilterRefreshSequence++;viewReady=false;Invalidate();}}
        public void SetAcceptedPositionErrorPercent(double percent){if(IsFinite(percent)&&percent>=0){if(acceptedPositionErrorPercent==percent)return;acceptedPositionErrorPercent=percent;RefreshPositionApproximationForTargets();NotifyApproximationDisplayChanged();}}
        public void SetRestoreOriginalMap(bool restore){ExecuteRestoreCommand(restore);}
        void ExecuteRestoreCommand(bool value){if(value&&!restoreCommandState)RestoreOriginalMapNow();restoreCommandState=value;}

        public void RestoreOriginalMapNow()
        {
            ClearCursorSelection();SuppressPositionApproximationForExactEdit();
            if(originalPulse.Length==0)
            {
                incident=initialIncident;reflection=initialReflection;reflectionSelected=initialReflectionSelected;ClampIndices();
                AlignCalibration();RecomputeFilteredCurves();UpdateDynamicAnalysis();ResetView();Invalidate();return;
            }
            pulse=(double[])originalPulse.Clone();
            incident=initialIncident=originalIncident;reflection=initialReflection=originalReflection;
            reflectionSelected=initialReflectionSelected=originalReflectionSelected;
            dtNs=originalDtNs;velocityMPerUs=originalVelocity;velocityConfigured=originalVelocityConfigured;cableLengthM=originalCableLength;
            ApplyProjectMetadataForMap(originalParameterRow);
            rawCharge=originalRawCharge;compensatedCharge=originalCompensatedCharge;correlation=originalCorrelation;confidence=originalConfidence;
            voltageKv=originalVoltageKv;phaseDeg=originalPhaseDeg;currentLocationM=originalLocationM;currentTimeDifferenceNs=originalTimeDifferenceNs;
            samePositionPulseCount=originalSamePositionPulseCount;boundaryDirection=originalBoundaryDirection;matrixDriven=true;
            NormalizeLoadedCoincidentConfidence();
            ValidateReflectionSelectionWindow();
            AlignCalibration();RecomputeFilteredCurves();compensatedCharge=originalCompensatedCharge;if(!reflectionSelected)SetUnselectedReflectionAnalysis();CountSamePositionPulses(currentLocationM);RestoreSequence++;ResetView();Invalidate();NotifyReviewAnalysisEdited("RestoreOriginal",false,true);
        }

        public double[] GetCurrentInformation()
        {
            return new[]{(double)currentPdMapId,(double)incident,reflectionSelected?(double)reflection:double.NaN,TimeDifferenceNs,LocationM,rawCharge,compensatedCharge,confidence,correlation,voltageKv,phaseDeg,(double)samePositionPulseCount};
        }
        public double[] GetCurrentWaveform(){return (double[])pulse.Clone();}
        public double[] GetCurrentFilteredWaveform(){return (double[])filteredPulse.Clone();}
        public double[] GetSelectedParameterRow()
        {
            ValidateReflectionSelectionWindow();
            double[] row=(double[])originalParameterRow.Clone();if(row.Length==0)return row;double start=Safe(row,31,0);if(row.Length>10)row[10]=start+incident;if(row.Length>11)row[11]=reflectionSelected?start+reflection:double.NaN;if(row.Length>12)row[12]=reflectionSelected?reflection-(double)incident:double.NaN;if(row.Length>13)row[13]=LocationM;if(row.Length>16)row[16]=compensatedCharge;if(row.Length>20)row[20]=incident;if(row.Length>21)row[21]=correlation/100.0;if(row.Length>33)row[33]=confidence/100.0;
            if(!reflectionSelected)ClearUnselectedPairColumns(row);
            return row;
        }
        public double[] GetOriginalParameterRow(){return (double[])originalParameterRow.Clone();}

        void ApplyParameterRow(double[] row,bool currentContract)
        {
            confidenceStatusCode="InputMatrix";confidenceDetails="";
            double sampleRate=Safe(row,7,double.NaN);if(IsFinite(sampleRate)&&sampleRate>0)dtNs=1e9/sampleRate;
            double length=Safe(row,5,double.NaN);if(IsFinite(length)&&length>0)cableLengthM=length;
            double velocity=Safe(row,6,double.NaN);if(IsFinite(velocity)&&velocity>0)velocityMPerUs=NormalizeVelocity(velocity);
            velocityConfigured=(IsFinite(velocity)&&velocity>0)||manualVelocityConfigured;
            if((!IsFinite(velocity)||velocity<=0)&&manualVelocityConfigured)velocityMPerUs=manualVelocityMPerUs;
            voltageKv=FiniteOr(Safe(row,2,0),0);phaseDeg=FiniteOr(Safe(row,3,0),0);
            rawCharge=FiniteOr(Safe(row,15,0),0);compensatedCharge=FiniteOr(Safe(row,16,rawCharge),rawCharge);
            if(currentContract)
            {
                correlation=IsFinite(Safe(row,21,double.NaN))?ToPercent(row[21]):double.NaN;confidence=row.Length>33&&IsFinite(row[33])?ToPercent(row[33]):double.NaN;
            }
            else
            {
                correlation=ToPercent(Safe(row,33,0));confidence=ToPercent(Safe(row,22,0));
            }
            double localIncident=Safe(row,20,double.NaN);
            double difference=Safe(row,12,double.NaN);
            double windowStart=Safe(row,31,double.NaN);
            if(!IsFinite(localIncident))
            {
                double absoluteIncident=Safe(row,10,0);
                localIncident=IsFinite(windowStart)?absoluteIncident-windowStart:absoluteIncident;
            }
            // A local fallback must NEVER be treated as a global source index.
            // A real reflection outside this crop is retained, not clamped to an edge.
            double absoluteReflection=Safe(row,11,double.NaN);
            double localReflection=IsSampleIndex(absoluteReflection)?(IsSampleIndex(windowStart)?absoluteReflection-windowStart:!currentContract?absoluteReflection:double.NaN):double.NaN;
            incident=initialIncident=IsLocalSampleIndex(localIncident)?(int)localIncident:0;
            reflectionSelected=initialReflectionSelected=IsLocalSampleIndex(localReflection);
            reflection=initialReflection=reflectionSelected?(int)localReflection:incident;
            boundaryDirection=reflection>=incident?1:-1;
            currentLocationM=reflectionSelected?FiniteOr(Safe(row,13,double.NaN),CalculatedLocation()):double.NaN;
            currentTimeDifferenceNs=reflectionSelected?Math.Abs(reflection-(double)incident)*dtNs:double.NaN;
            if(!reflectionSelected)SetUnselectedReflectionAnalysis();
            NormalizeLoadedCoincidentConfidence();
        }

        void CaptureOriginalSnapshot(double[] row)
        {
            originalPulse=(double[])pulse.Clone();originalParameterRow=(double[])row.Clone();
            originalIncident=incident;originalReflection=reflection;originalReflectionSelected=reflectionSelected;originalBoundaryDirection=boundaryDirection;originalDtNs=dtNs;originalVelocity=velocityMPerUs;originalCableLength=cableLengthM;
            // Restore the provenance together with its numeric value. A constructor
            // fallback is not configured merely because a later manual input was.
            originalVelocityConfigured=velocityConfigured;
            originalRawCharge=Safe(row,15,rawCharge);originalCompensatedCharge=Safe(row,16,compensatedCharge);originalCorrelation=correlation;originalConfidence=confidence;
            originalVoltageKv=voltageKv;originalPhaseDeg=phaseDeg;originalLocationM=currentLocationM;originalTimeDifferenceNs=currentTimeDifferenceNs;
            originalSamePositionPulseCount=samePositionPulseCount;
        }

        void BuildPreviousOverlay()
        {
            if(standaloneOverlay){showOverlay=showPreviousPositioningResults&&positions.Length>0;return;}
            double[,] source=previousOverlayExplicit?previousPositioningMatrix:positioningMatrix;
            if(ReferenceEquals(builtOverlayMatrix,source)&&ReferenceEquals(builtPositions,positions)&&ReferenceEquals(builtCharges,charges)){showOverlay=showPreviousPositioningResults&&positions.Length>0;return;}
            builtOverlayMatrix=source;
            if(source==null||source.GetLength(0)==0){positions=charges=overlayPhases=Array.Empty<double>();showOverlay=false;builtPositions=positions;builtCharges=charges;SetOverlayClassificationSource(null,null);return;}
            int rows=source.GetLength(0),cols=source.GetLength(1);var p=new List<double>();var q=new List<double>();var ph=new List<double>();var sourceRows=new List<int>();
            bool anyAccepted=cols>35&&AnyAccepted(source);
            for(int r=0;r<rows;r++)
            {
                if(anyAccepted&&source[r,35]==0)continue;
                double x=cols>13?source[r,13]:(cols>0?source[r,0]:double.NaN);
                double y=cols>16&&IsFinite(source[r,16])?source[r,16]:(cols>15?source[r,15]:(cols>1?source[r,1]:double.NaN));
                if(IsFinite(x)&&IsFinite(y)){p.Add(x);q.Add(y);ph.Add(cols>0&&IsFinite(source[r,0])?source[r,0]:0);sourceRows.Add(r);}
            }
            positions=p.ToArray();charges=q.ToArray();overlayPhases=ph.ToArray();showOverlay=showPreviousPositioningResults&&positions.Length>0;
            builtPositions=positions;builtCharges=charges;
            SetOverlayClassificationSource(source,sourceRows.ToArray());
        }

        int FindParameterRow(double[,] matrix,int id)
        {
            if(reviewSourceRowOverride>=0&&reviewSourceRowOverride<matrix.GetLength(0))return reviewSourceRowOverride;
            int rows=matrix.GetLength(0),cols=matrix.GetLength(1),first=-1,firstAccepted=-1;
            for(int r=0;r<rows;r++)
            {
                if(cols<=9||!IsFinite(matrix[r,9])||(int)Math.Round(matrix[r,9])!=id)continue;
                if(first<0)first=r;if(cols>35&&matrix[r,35]!=0&&firstAccepted<0)firstAccepted=r;
                if(r==id)return r;
            }
            return firstAccepted>=0?firstAccepted:first;
        }

        int ResolveMatchingRow(int parameterRow,int id)
        {
            int rows=matchingMatrix.GetLength(0),positionRows=positioningMatrix.GetLength(0);
            if(rows==positionRows||rows==positionRows*3)return parameterRow<rows?parameterRow:-1;
            return id>=0&&id<rows?id:-1;
        }

        void CountSamePositionPulses(double location)
        {
            samePositionPulseCount=CountLocationsNear(location,Math.Abs(cableLengthM)*acceptedPositionErrorPercent/100.0+1e-9);
        }

        void RecomputeFilteredCurves()
        {
            double fs=dtNs>0?1e9/dtNs:0;string a,b;
            filteredPulse=pulseFilterCache.Apply(pulse,fs,filterParameters,out a);
            filteredCalibration=calibrationFilterCache.Apply(alignedCalibration,fs,filterParameters,out b);
            RefitFilteredDamping();
            FilterStatus=a+" | "+b+" | filtered-calibration damping: "+filteredDampingStatus;
        }

        void UpdateDynamicAnalysis()
        {
            ValidateReflectionSelectionWindow();
            if(!matrixDriven)return;
            if(!reflectionSelected){SetUnselectedReflectionAnalysis();CountSamePositionPulses(double.NaN);return;}
            currentTimeDifferenceNs=Math.Abs(reflection-(double)incident)*dtNs;currentLocationM=CalculatedLocation();
            CountSamePositionPulses(currentLocationM);
            UpdateConfidenceEvidence();
            double ai=Math.Abs(ValueAt(alignedAttenuation,incident)),ar=Math.Abs(ValueAt(alignedAttenuation,reflection));
            if(ProjectCalibrationEnabled)UpdateProjectCompensation();
            else if(ai>1e-12&&ar>1e-12)compensatedCharge=Math.Round(rawCharge*ai/ar);
        }

        double CalculatedLocation(){return reflectionSelected?PositionForSampleDelta(reflection-(double)incident):double.NaN;}
        int CableBoundaryIndex(){double delta;if(!TrySampleDeltaForPosition(mirrorLocation?cableLengthM:0,out delta))return -1;double value=incident+boundaryDirection*Math.Round(delta);return !IsFinite(value)||value<int.MinValue||value>int.MaxValue?-1:(int)value;}
        public void SetLocationMatrix(double[,] matrix)
        {
            if(matrix==null||matrix.GetLength(0)==0||matrix.GetLength(1)<36)return;
            positioningMatrix=CloneMatrix(matrix);standaloneOverlay=false;previousOverlayExplicit=false;previousPositioningMatrix=null;
            int rows=matrix.GetLength(0); bool selected=false; for(int r=0;r<rows;r++)if(matrix[r,35]!=0){selected=true;break;}
            var p=new List<double>();var q=new List<double>();var ph=new List<double>();var sourceRows=new List<int>();int info=-1;
            for(int r=0;r<rows;r++)
            {
                if(selected&&matrix[r,35]==0)continue;if(info<0)info=r;
                double x=matrix[r,13],y=matrix[r,16]!=0?matrix[r,16]:matrix[r,15];if(!IsFinite(x)||!IsFinite(y))continue;
                p.Add(x);q.Add(y);ph.Add(IsFinite(matrix[r,0])?matrix[r,0]:0);sourceRows.Add(r);
            }
            positions=p.ToArray();charges=q.ToArray();overlayPhases=ph.ToArray();
            builtOverlayMatrix=positioningMatrix;builtPositions=positions;builtCharges=charges;SetOverlayClassificationSource(positioningMatrix,sourceRows.ToArray());
            showOverlay=showPreviousPositioningResults&&positions.Length>0;
            if(info>=0)
            {
                ClearPositionApproximation();
                currentParameterRowIndex=info;currentPdMapId=(int)Math.Round(matrix[info,9]);double[] row=ExtractMatrixRow(matrix,info,matrix.GetLength(1));ApplyParameterRow(row,IsCurrentContract(row));matrixDriven=true;
                SelectProjectCalibrationForMap(row);
                ClampIndices();AlignCalibration();RecomputeFilteredCurves();CountSamePositionPulses(currentLocationM);CaptureOriginalSnapshot(row);
                if(ConnectingCableLengthM>0||connectingGeometryReload)RefreshConnectingPositionOnly();else if(MixedCableCalculation)UpdateDynamicAnalysis();ValidateReflectionSelectionWindow();if(!connectingGeometryReload)CommitPositionApproximation();
            }
            ResetView();Invalidate();
        }
        public string RenderPdMapWithLocationMatrix(double[,] locationMatrix,double[] pulseWaveform,int initialIncidentIndex,int initialReflectionIndex,double sampleIntervalNs,double waveVelocityMPerUs,double cableLengthMeters,double[] calibrationWaveform,double[] calibrationDecay,int calibrationPeakIndex,bool calibrationVisible,bool attenuationVisible,double calibrationAlpha,double attenuationAlpha,int pointsBefore,int pointsAfter,int[] argbColors,double[] fallbackPositionsM,double[] fallbackChargesPc,double[] connectorPositionsM,bool locationOverlayVisible,double compensatedChargePc,double correlationPercent,double reflectionConfidencePercent,bool englishVersion,string outputDirectory,string outputName)
        {
            SetLanguage(englishVersion);SetWaveform(pulseWaveform,sampleIntervalNs);SetInitialIndices(initialIncidentIndex,initialReflectionIndex);SetCableParameters(waveVelocityMPerUs,cableLengthMeters);SetCalibration(calibrationWaveform,calibrationDecay,calibrationPeakIndex);SetCalibrationDisplay(calibrationVisible,attenuationVisible,calibrationAlpha,attenuationAlpha);SetSelectionWindow(pointsBefore,pointsAfter);SetColors(argbColors);SetConnectors(connectorPositionsM);SetAnalysisValues(compensatedChargePc,correlationPercent,reflectionConfidencePercent);
            if(locationMatrix!=null&&locationMatrix.GetLength(0)>0&&locationMatrix.GetLength(1)>=36)SetLocationMatrix(locationMatrix);else SetLocationOverlay(fallbackPositionsM,fallbackChargesPc,locationOverlayVisible);
            showPreviousPositioningResults=showOverlay=locationOverlayVisible;ResetView(); if(string.IsNullOrWhiteSpace(outputDirectory))outputDirectory=Path.Combine(Path.GetTempPath(),"LabVIEWPdMap");if(string.IsNullOrWhiteSpace(outputName))outputName="PDmap.png";if(!outputName.EndsWith(".png",StringComparison.OrdinalIgnoreCase))outputName+=".png";SavePng(Path.Combine(outputDirectory,outputName),2400,1250);return LastOutputPath;
        }

        public string RenderPdMapBySamplingRateWithLocationMatrix(double[,] locationMatrix,double[] pulseWaveform,int initialIncidentIndex,int initialReflectionIndex,double samplingRateHz,double waveVelocityMPerUs,double cableLengthMeters,bool mirror,double[] calibrationWaveform,double[] calibrationDecay,int calibrationPeakIndex,bool calibrationVisible,bool attenuationVisible,double calibrationAlpha,double attenuationAlpha,int pointsBefore,int pointsAfter,int[] argbColors,double[] fallbackPositionsM,double[] fallbackChargesPc,double[] connectorPositionsM,bool locationOverlayVisible,double compensatedChargePc,double correlationPercent,double reflectionConfidencePercent,bool englishVersion,string outputDirectory,string outputName)
        {
            SetLanguage(englishVersion);SetWaveformBySamplingRate(pulseWaveform,samplingRateHz);SetInitialIndices(initialIncidentIndex,initialReflectionIndex);SetCableParameters(waveVelocityMPerUs,cableLengthMeters);SetMirrorLocation(mirror);SetCalibration(calibrationWaveform,calibrationDecay,calibrationPeakIndex);SetCalibrationDisplay(calibrationVisible,attenuationVisible,calibrationAlpha,attenuationAlpha);SetSelectionWindow(pointsBefore,pointsAfter);SetColors(argbColors);SetConnectors(connectorPositionsM);SetAnalysisValues(compensatedChargePc,correlationPercent,reflectionConfidencePercent);
            if(locationMatrix!=null&&locationMatrix.GetLength(0)>0&&locationMatrix.GetLength(1)>=36)SetLocationMatrix(locationMatrix);else SetLocationOverlay(fallbackPositionsM,fallbackChargesPc,locationOverlayVisible);
            showPreviousPositioningResults=showOverlay=locationOverlayVisible;ResetView();if(string.IsNullOrWhiteSpace(outputDirectory))outputDirectory=Path.Combine(Path.GetTempPath(),"LabVIEWPdMap");if(string.IsNullOrWhiteSpace(outputName))outputName="PDmap.png";if(!outputName.EndsWith(".png",StringComparison.OrdinalIgnoreCase))outputName+=".png";SavePng(Path.Combine(outputDirectory,outputName),2400,1250);return LastOutputPath;
        }
        void ClampIndices(){if(pulse.Length==0){incident=reflection=initialIncident=initialReflection=0;reflectionSelected=initialReflectionSelected=false;return;}incident=Limit(incident,0,pulse.Length-1);initialIncident=Limit(initialIncident,0,pulse.Length-1);if(!matrixDriven){reflection=Limit(reflection,0,pulse.Length-1);initialReflection=Limit(initialReflection,0,pulse.Length-1);}if(!reflectionSelected)reflection=incident;if(!initialReflectionSelected)initialReflection=initialIncident;}
        HVX.SignalEvidence.SixthCalibrationReference alignmentDampingReference;
        bool alignmentDampingQualified;
        double alignmentDampingDtNs=double.NaN;
        void AlignCalibration()
        {
            if(ProjectCalibrationEnabled){AlignProjectCalibration();return;}
            int displayIncident = DisplayIncidentPeak();
            HVX.SignalEvidence.SixthCalibrationReference rawReference;
            bool rawQualified=TryRawDampingReference(out rawReference);
            bool sameAlignment=ReferenceEquals(alignmentPulse,pulse)&&ReferenceEquals(alignmentCalibration,calibration)
                &&alignmentIncident==displayIncident&&alignmentPeak==calibrationPeak;
            bool sameDecay=sameAlignment&&alignmentReflection==reflection&&ReferenceEquals(alignmentAttenuation,attenuation)&&
                ReferenceEquals(alignmentDampingReference,rawReference)&&alignmentDampingQualified==rawQualified&&alignmentDampingDtNs.Equals(dtNs);
            if(sameDecay)return;
            // Reflection movement changes the fitted envelope, not the aligned calibration.
            // Without a calibration waveform both curves remain zero for every cursor position.
            bool zeroCurves=calibration.Length==0&&alignmentCalibration!=null&&alignmentCalibration.Length==0&&alignedCalibration.Length==pulse.Length;
            alignmentPulse=pulse;alignmentCalibration=calibration;alignmentAttenuation=attenuation;
            alignmentIncident=displayIncident;alignmentReflection=reflection;alignmentPeak=calibrationPeak;
            alignmentDampingReference=rawReference;alignmentDampingQualified=rawQualified;alignmentDampingDtNs=dtNs;
            hasDampingCurve=false;
            if(zeroCurves)return;
            if(!sameAlignment){alignedCalibration=new double[pulse.Length];hasAlignedCalibrationCurve=false;}
            alignedAttenuation=new double[pulse.Length];
            if(pulse.Length==0||!HasCalibrationWaveform)return;
            int pk=Limit(calibrationPeak,0,calibration.Length-1);
            double cp=calibration[pk],pp=pulse[displayIncident];
            // A retained waveform may have a small real acquisition gap at
            // either alignment anchor. It is unknown evidence, not a zero or
            // a valid sign; Math.Sign(NaN) would otherwise abort the load.
            if(!IsFinite(cp)||!IsFinite(pp))return;
            double scale=Math.Abs(cp)<1e-15?1:Math.Abs(pp/cp);
            if(Math.Sign(cp)!=Math.Sign(pp))scale=-scale;
            if(!IsFinite(scale))return;
            if(!sameAlignment)for(int i=0;i<calibration.Length;i++)
            {
                int j=displayIncident+(i-pk);
                if(j>=0&&j<pulse.Length)alignedCalibration[j]=calibration[i]*scale;
            }
            if(!sameAlignment)
            {
                hasAlignedCalibrationCurve=HasNonzeroFiniteCurve(alignedCalibration);
                if(!hasAlignedCalibrationCurve)Array.Clear(alignedCalibration,0,alignedCalibration.Length);
            }
            if(hasAlignedCalibrationCurve&&rawQualified)FitAttenuationCurve(pp,scale);
            hasDampingCurve=HasNonzeroFiniteCurve(alignedAttenuation);
        }
        void FitAttenuationCurve(double startValue,double scale)
        {
            if(!IsFinite(startValue)||startValue==0||!IsFinite(scale))return;
            double slope;if(!TryConfidenceDecaySlope(out slope))return;
            int end=CableBoundaryIndex();if(end<=incident)end=pulse.Length-1;
            end=Math.Min(end,alignedAttenuation.Length-1);
            // Fixed CAL sample-time reference, shared with manual evidence.
            // Moving the reflection cursor must not stretch the decay curve.
            int displayIncident=DisplayIncidentPeak();
            for(int j=displayIncident;j<=end;j++)alignedAttenuation[j]=startValue*Math.Exp(slope*(j-(double)displayIncident)*dtNs);
        }
        int SnapExtremum(int target)
        {
            double[] values=!Visible(0,true)&&Visible(3,false)?filteredPulse:pulse;
            if(values.Length<3||(!Visible(0,true)&&!Visible(3,false)))return Limit(target,0,Math.Max(0,pulse.Length-1));int radius=Math.Max(12,n1+n2);int a=Math.Max(1,target-radius),b=Math.Min(values.Length-2,target+radius),best=-1;double bestScore=double.MinValue;
            for(int i=a;i<=b;i++){bool ex=(values[i]>=values[i-1]&&values[i]>values[i+1])||(values[i]<=values[i-1]&&values[i]<values[i+1]);if(!ex)continue;double prominence=Math.Abs(values[i]-(values[i-1]+values[i+1])*.5);double score=(prominence+Math.Abs(values[i])*.12)/(1.0+Math.Abs(i-target)*.055);if(score>bestScore){best=i;bestScore=score;}}
            if(best>=0)return best;best=a;for(int i=a+1;i<=b;i++)if(Math.Abs(values[i])>Math.Abs(values[best]))best=i;return best;
        }
        // Keep the existing void API for hosts; callers needing user feedback
        // must inspect the outcome rather than treating a silent no-op as success.
        public void MoveReflectionToIndex(int targetIndex){MoveReflectionToIndexWithResult(targetIndex);}
        public ReflectionMoveResult MoveReflectionToIndexWithResult(int targetIndex)
        {
            if(capacitorStatisticsMode)return ReflectionMoveResult.UnsupportedMode;
            if(pulse.Length==0)return ReflectionMoveResult.NoWaveform;
            int next=ConstrainReflectionSelection(targetIndex);
            if(next<0){Invalidate();NotifyReviewAnalysisEdited("ReflectionWindowUnavailable",draggingCursor!=0);return ReflectionMoveResult.SelectionUnavailable;}
            if(next==reflection&&reflectionSelected)return ReflectionMoveResult.AlreadyAtTarget;
            SuppressPositionApproximationForExactEdit();reflection=next;reflectionSelected=true;
            AlignCalibration();RecomputeFilteredCurves();UpdateDynamicAnalysis();Invalidate();NotifyReviewAnalysisEdited("Reflection",draggingCursor!=0);
            return reflectionSelected&&reflection==next?ReflectionMoveResult.Moved:ReflectionMoveResult.SelectionUnavailable;
        }
        public void MoveIncidentToIndex(int targetIndex){if(pulse.Length==0)return;int next=Limit(targetIndex,0,pulse.Length-1);SuppressPositionApproximationForExactEdit();if(next==incident)return;incident=next;AlignCalibration();RecomputeFilteredCurves();UpdateDynamicAnalysis();Invalidate();NotifyReviewAnalysisEdited("Incident",draggingCursor!=0);}
        public void ResetView(){EndCursorDrag("view reset");viewReady=false;Invalidate();}
        public void ResetReflectionToInitial(){EndCursorDrag();if(reflection==initialReflection&&reflectionSelected==initialReflectionSelected)return;SuppressPositionApproximationForExactEdit();reflection=initialReflection;reflectionSelected=initialReflectionSelected;AlignCalibration();RecomputeFilteredCurves();UpdateDynamicAnalysis();Invalidate();NotifyReviewAnalysisEdited("ResetReflection");}
        public void ResetIncidentToInitial(){EndCursorDrag();if(incident==initialIncident)return;SuppressPositionApproximationForExactEdit();incident=initialIncident;AlignCalibration();RecomputeFilteredCurves();UpdateDynamicAnalysis();Invalidate();NotifyReviewAnalysisEdited("ResetIncident");}

        protected override void OnPaint(PaintEventArgs e){base.OnPaint(e);e.Graphics.TextRenderingHint=System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;Draw(e.Graphics,ClientRectangle,true);DrawDisplayOptionsFrame(e.Graphics);}
        void Draw(Graphics g,Rectangle canvas,bool interactive)
        {
            ValidateReflectionSelectionWindow();
            g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(HVX.PdReview.ReviewTheme.Surface);if(canvas.Width<280||canvas.Height<210){if(interactive){connectorHitAreas.Clear();connectorHitBounds=RectangleF.Empty;cableEndpointMarkerBounds.Clear();cableMaterialLabelBounds.Clear();ClearConnectorToolTip();}return;}
            Rectangle p=CalculatePlotLayout(g,canvas).Plot;if(interactive)lastPlot=p;
            using(var section=new SolidBrush(HVX.PdReview.ReviewTheme.SurfaceAlt))g.FillRectangle(section,canvas.Left,canvas.Top,canvas.Width,28);
            if(showTitle)using(var tf=F(canvas,20,FontStyle.Bold))using(var tb=new SolidBrush(ForeColor))using(var sf=new StringFormat{Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap})g.DrawString(L("Pulse discharge PDmap","脉冲放电 PDmap"),tf,tb,new RectangleF(p.Left,canvas.Top+4,p.Width,tf.GetHeight(g)+7),sf);
            DrawLegend(g,p,canvas);
            if(pulse.Length==0){if(interactive){connectorHitAreas.Clear();connectorHitBounds=RectangleF.Empty;cableEndpointMarkerBounds.Clear();cableMaterialLabelBounds.Clear();ClearConnectorToolTip();}using(var f=F(canvas,18,FontStyle.Regular))g.DrawString(L("No waveform data","无波形数据"),f,Brushes.Gray,p.Left+20,p.Top+20);DrawAxes(g,p,canvas);return;}
            EnsureView();using(var bg=new SolidBrush(showOverlay?Color.FromArgb(244,250,247):HVX.PdReview.ReviewTheme.Surface))g.FillRectangle(bg,p);if(!capacitorStatisticsMode)DrawCablePropagationRange(g,p);DrawGrid(g,p,canvas);
            GraphicsState waveClip=g.Save();g.SetClip(p);if(showOverlay){DrawOverlay(g,p,canvas);DrawWaveCompressed(g,p,canvas);}else DrawWaveNormal(g,p,canvas);g.Restore(waveClip);
            bottomLabelBounds.Clear();DrawCursors(g,p,canvas,interactive);if(!capacitorStatisticsMode)DrawConnectors(g,p,canvas,interactive);else{connectorHitAreas.Clear();connectorHitBounds=RectangleF.Empty;cableEndpointMarkerBounds.Clear();cableMaterialLabelBounds.Clear();}DrawAxes(g,p,canvas);using(var pen=new Pen(HVX.PdReview.ReviewTheme.SecondaryInk,1f))g.DrawRectangle(pen,p);
            // Transparent information text is the final chart layer, so cursor and
            // connector strokes cannot repaint the endpoint/range explanation.
            DrawInfo(g,p,canvas);
            if(interactive)DrawBoxZoomSelection(g,p);
        }
        void DrawLegend(Graphics g,Rectangle p,Rectangle c)
        {
            using(var f=F(c,13,FontStyle.Regular))using(var br=new SolidBrush(ForeColor))
            {
                var layout=CalculatePlotLayout(g,c);float x=p.Left,y=layout.LegendTop;
                var items=LegendItems(c.Width<520);
                float legendWidth=0;
                foreach(var item in items)legendWidth+=28+g.MeasureString(item.Key,f).Width+10;
                if(legendWidth<=layout.LegendRight-p.Left)x=layout.LegendRight-legendWidth;
                if(layout.HvNameBounds.Width>0&&layout.HvNameBounds.Bottom<=layout.CursorLabelTop-3)
                    using(var hvFormat=new StringFormat{Alignment=StringAlignment.Far,Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap})
                        g.DrawString(DisplayedHvName,f,br,layout.HvNameBounds,hvFormat);
                using(var sf=new StringFormat{Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap})
                foreach(var item in items)
                {
                    float w=28+g.MeasureString(item.Key,f).Width+10;
                    if(x>p.Left&&x+w>layout.LegendRight){x=p.Left;y+=layout.LineHeight+3;}
                    if(y+layout.LineHeight>layout.CursorLabelTop-3)break;
                    if(layout.HvNameBounds.Width>0&&y+layout.LineHeight>layout.HvNameBounds.Top&&y<layout.HvNameBounds.Bottom&&layout.LegendRight==p.Right)break;
                    bool damping=item.Key.IndexOf("阻尼",StringComparison.Ordinal)>=0||item.Key.IndexOf("Damping",StringComparison.OrdinalIgnoreCase)>=0;
                    bool cal=item.Key.IndexOf("校准",StringComparison.Ordinal)>=0||item.Key.IndexOf("Calibration",StringComparison.OrdinalIgnoreCase)>=0;
                    double alpha=damping?attenuationOpacity:cal?calibrationOpacity:1;
                    using(var pen=new Pen(Color.FromArgb((int)(255*alpha),item.Value),damping?1f:cal?.9f:pulseLineWidth))
                    {if(damping)pen.DashStyle=DashStyle.Dash;g.DrawLine(pen,x,y+layout.LineHeight/2,x+21,y+layout.LineHeight/2);}
                    g.DrawString(item.Key,f,br,new RectangleF(x+26,y,Math.Max(1,layout.LegendRight-x-26),layout.LineHeight),sf);x+=w;
                }
            }
        }
        void EnsureView()
        {
            if(viewReady)return;viewX0=0;viewX1=Math.Max(1,pulse.Length-1);double lo=double.MaxValue,hi=double.MinValue;
            if(Visible(0,true))IncludeRange(pulse,ref lo,ref hi);if(Visible(1,true)&&showCalibration&&hasAlignedCalibrationCurve)IncludeRange(alignedCalibration,ref lo,ref hi);if(Visible(2,true)&&showAttenuation&&HasDampingCurve)IncludeRange(alignedAttenuation,ref lo,ref hi);if(Visible(3,false))IncludeRange(filteredPulse,ref lo,ref hi);if(Visible(4,false)&&showCalibration&&hasAlignedCalibrationCurve)IncludeRange(filteredCalibration,ref lo,ref hi);if(Visible(5,false)&&showAttenuation&&HasFilteredDampingCurve)IncludeRange(filteredAttenuation,ref lo,ref hi);
            if(lo==double.MaxValue||hi==double.MinValue){lo=-1;hi=1;}if(lo==hi){lo-=1;hi+=1;}double pad=(hi-lo)*.12;viewY0=lo-pad;viewY1=hi+pad;if(showOverlay&&charges.Length>0){viewY0=0;viewY1=NiceMax(Max(charges)*1.12);}viewReady=true;
        }
        void DrawGrid(Graphics g,Rectangle p,Rectangle c){var layout=CalculatePlotLayout(g,c);using(var pen=new Pen(HVX.PdReview.ReviewTheme.Grid,1)){foreach(double tick in layout.XTicks){float x=X(tick*1000/dtNs,p);g.DrawLine(pen,x,p.Top,x,p.Bottom);}foreach(double tick in layout.YTicks){float y=Y(tick,p);g.DrawLine(pen,p.Left,y,p.Right,y);}}if(viewY0<0&&viewY1>0)using(var zero=new Pen(HVX.PdReview.ReviewTheme.Border,1))g.DrawLine(zero,p.Left,Y(0,p),p.Right,Y(0,p));}
        void DrawAxes(Graphics g,Rectangle p,Rectangle c)
        {
            using(var f=F(c,15,FontStyle.Regular))using(var b=new SolidBrush(ForeColor))
            {
                var layout=CalculatePlotLayout(g,c);
                foreach(double tick in layout.XTicks){double ix=tick*1000/dtNs;string s=AxisTickX(ix);SizeF z=g.MeasureString(s,f);g.DrawString(s,f,b,ClampF(X(ix,p)-z.Width/2,c.Left+2,c.Right-z.Width-2),p.Bottom+5);}
                foreach(double tick in layout.YTicks){string s=AxisTickY(tick);SizeF z=g.MeasureString(s,f);g.DrawString(s,f,b,p.Left-z.Width-7,Y(tick,p)-z.Height/2);}
                string x=L("Time / us","时间 / μs"),y=showOverlay?(L("Discharge / pC","放电量 / pC")):(L("Amplitude / pC","幅值 / pC"));SizeF sx=g.MeasureString(x,f);g.DrawString(x,f,b,p.Left+(p.Width-sx.Width)/2,p.Bottom+layout.LineHeight+9);GraphicsState st=g.Save();g.TranslateTransform(layout.AxisCenterX,p.Top+p.Height/2);g.RotateTransform(-90);SizeF sy=g.MeasureString(y,f);if(sy.Width>p.Height-8){y="pC";sy=g.MeasureString(y,f);}if(sy.Width<=p.Height-4)g.DrawString(y,f,b,-sy.Width/2,-sy.Height/2);g.Restore(st);
            }
        }
        void DrawWaveNormal(Graphics g,Rectangle p,Rectangle c)
        {
            DrawAuxiliaryCurves(g,p,p,viewY0,viewY1);
            if(Visible(0,true))DrawSeries(g,p,pulse,pulseColor,pulseLineWidth,1);
            if(Visible(3,false))DrawSeries(g,p,filteredPulse,filteredPulseColor,pulseLineWidth,1);
            DrawVisiblePulseWindows(g,p,p,viewY0,viewY1);
        }
        void DrawWaveCompressed(Graphics g,Rectangle p,Rectangle c)
        {
            double lo,hi;PulseRange(out lo,out hi);Rectangle band=new Rectangle(p.Left,p.Bottom-(int)(p.Height*.21),p.Width,(int)(p.Height*.19));
            DrawAuxiliaryCurves(g,p,band,lo,hi);
            if(Visible(0,true))DrawSeriesMapped(g,p,pulse,pulseColor,pulseLineWidth,1,lo,hi,band);
            if(Visible(3,false))DrawSeriesMapped(g,p,filteredPulse,filteredPulseColor,pulseLineWidth,1,lo,hi,band);
            DrawVisiblePulseWindows(g,p,band,lo,hi);
        }
        void DrawOverlay(Graphics g,Rectangle p,Rectangle c)
        {
            DrawClassifiedOverlay(g,p);
        }
        void DrawCursors(Graphics g,Rectangle p,Rectangle c,bool interactive)
        {
            incidentLabelBounds=reflectionLabelBounds=RectangleF.Empty;
            DrawCursor(g,p,c,incident,incidentColor,L("Incident","入射波"),1,interactive);if(ReflectionWithinWaveform)DrawCursor(g,p,c,reflection,reflectionColor,L("Reflection","反射波"),2,interactive);
        }
        RectangleF incidentLabelBounds,reflectionLabelBounds;
        void DrawCursor(Graphics g,Rectangle p,Rectangle c,int index,Color color,string label,int cursorId,bool interactive)
        {
            if(index<viewX0||index>viewX1)return;
            float x=X(index,p);using(var pen=new Pen(color,1.1f)){pen.DashStyle=DashStyle.Dash;g.DrawLine(pen,x,p.Top,x,p.Bottom);}
            DrawCursorHandle(g,p,x,color,cursorId,interactive);
            using(var f=F(c,13,FontStyle.Bold))using(var b=new SolidBrush(color))
            {
                string text=label;SizeF size=g.MeasureString(text,f);
                var layout=CalculatePlotLayout(g,c);
                bool nearby=ReflectionWithinWaveform&&Math.Abs(X(incident,p)-X(reflection,p))<size.Width+30;
                bool before=nearby?cursorId==1:x+12+size.Width>p.Right;
                float left=before?Math.Max(c.Left+4,x-12-size.Width):x+12;
                float right=before?x-12:Math.Min(c.Right-4,left+size.Width);
                float top=p.Top-size.Height-2;
                var bounds=new RectangleF(left,Math.Max(layout.CursorLabelTop,top),Math.Max(0,right-left),size.Height);
                if(cursorId==1)incidentLabelBounds=bounds;else reflectionLabelBounds=bounds;
                using(var sf=new StringFormat{Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap})
                    if(bounds.Width>0)g.DrawString(text,f,b,bounds,sf);
            }
        }
        void DrawInfo(Graphics g,Rectangle p,Rectangle c)
        {
            DrawInformationSummary(g,p,c);
        }
        void DrawSeries(Graphics g,Rectangle p,double[] a,Color color,float width,double alpha){DrawBatchedSeries(g,p,a,color,width,alpha,p,viewY0,viewY1,0,a.Length-1,true);}
        void DrawSeriesMapped(Graphics g,Rectangle p,double[] a,Color color,float width,double alpha,double lo,double hi,Rectangle band){DrawBatchedSeries(g,p,a,color,width,alpha,band,lo,hi,0,a.Length-1,true);}
        float X(double v,Rectangle p)=>p.Left+(float)((v-viewX0)/(viewX1-viewX0)*p.Width);
        float Y(double v,Rectangle p)=>p.Bottom-(float)((v-viewY0)/(viewY1-viewY0)*p.Height);
        float MapY(double v,double lo,double hi,Rectangle r)=>r.Bottom-(float)((v-lo)/(hi-lo)*r.Height);
        double DataX(int px,Rectangle p)=>viewX0+(px-p.Left)*(viewX1-viewX0)/p.Width;
        double DataY(int py,Rectangle p)=>viewY1-(py-p.Top)*(viewY1-viewY0)/p.Height;

        double PositionToWaveIndex(double positionM)
        {
            if(cableLengthM<=0)return incident;double delta;return TrySampleDeltaForPosition(Clamp(positionM,0,cableLengthM),out delta)?incident+boundaryDirection*delta:double.NaN;
        }
        new bool Visible(int index,bool fallback)
        {
            bool value=curveVisibility!=null&&index>=0&&index<curveVisibility.Length?curveVisibility[index]:fallback;
            if(index==1||index==2)return value&&Visible(0,true);
            if(index==4||index==5)return value&&Visible(3,false);
            return value;
        }
        static void IncludeRange(double[] values,ref double lo,ref double hi){if(values==null)return;foreach(double v in values)if(IsFinite(v)){if(v<lo)lo=v;if(v>hi)hi=v;}}
        static double[] Sanitize(double[] source){if(source==null)return Array.Empty<double>();double[] result=(double[])source.Clone();for(int i=0;i<result.Length;i++)if(!IsFinite(result[i]))result[i]=0;return result;}
        static bool[] NormalizeVisibility(bool[] source)
        {
            bool[] d={true,false,false,false,false,false,true,true};
            if(source!=null)for(int i=0;i<Math.Min(d.Length,source.Length);i++)d[i]=source[i];
            // The filtered master wins an ambiguous simultaneous request; never mix groups.
            if(d[3])d[0]=false;
            if(!d[0])d[1]=d[2]=false;
            if(!d[3])d[4]=d[5]=false;
            return d;
        }
        static double[] NormalizeFilters(double[] source){double[] d={0,50000,7000000,4,5,2};if(source==null)return d;for(int i=0;i<Math.Min(d.Length,source.Length);i++)if(IsFinite(source[i]))d[i]=source[i];return d;}
        static bool IsUsableMatrix(double[,] matrix,int columns){return matrix!=null&&matrix.GetLength(0)>0&&matrix.GetLength(1)>=columns;}
        static double[,] CloneMatrix(double[,] source){if(source==null)return null;return (double[,])source.Clone();}
        static bool AnyAccepted(double[,] matrix){if(matrix==null||matrix.GetLength(1)<=35)return false;for(int r=0;r<matrix.GetLength(0);r++)if(matrix[r,35]!=0)return true;return false;}
        static double[] ExtractMatrixRow(double[,] matrix,int row,int count){int n=Math.Min(count,matrix.GetLength(1));double[] result=new double[n];for(int i=0;i<n;i++)result[i]=matrix[row,i];return result;}
        static int ExpectedWaveformLength(double[] row,int available){double s=Safe(row,31,double.NaN),e=Safe(row,32,double.NaN);if(IsFinite(s)&&IsFinite(e)&&e>=s){int n=(int)Math.Round(e-s+1);if(n>0)return Math.Min(n,available);}return available;}
        static double[] ExtractFiniteWaveform(double[,] matrix,int row,int expected)
        {
            if(matrix==null||row<0||row>=matrix.GetLength(0))return Array.Empty<double>();
            int n=Math.Min(matrix.GetLength(1),Math.Max(0,expected));
            while(n>0&&!IsFinite(matrix[row,n-1]))n--;
            if(n==0)return Array.Empty<double>();
            // Internal missing samples retain their original time index. Never
            // truncate all later samples at the first hole or replace gaps by zero.
            double[] result=new double[n];for(int i=0;i<n;i++)result[i]=IsFinite(matrix[row,i])?matrix[row,i]:double.NaN;return result;
        }
        static int IndexOfAbsolutePeak(double[] values){if(values==null||values.Length==0)return 0;int best=-1;for(int i=0;i<values.Length;i++)if(IsFinite(values[i])&&(best<0||Math.Abs(values[i])>Math.Abs(values[best])))best=i;return best<0?0:best;}
        static bool IsCurrentContract(double[] row){double s=Safe(row,31,double.NaN),e=Safe(row,32,double.NaN);return IsFinite(s)&&IsFinite(e)&&s>=0&&e>=s&&Math.Abs(s-Math.Round(s))<1e-6&&Math.Abs(e-Math.Round(e))<1e-6;}
        static double Safe(double[] values,int index,double fallback){return values!=null&&index>=0&&index<values.Length&&IsFinite(values[index])?values[index]:fallback;}
        static double FiniteOr(double value,double fallback){return IsFinite(value)?value:fallback;}
        static bool IsFinite(double value){return !double.IsNaN(value)&&!double.IsInfinity(value);}
        static double ToPercent(double value){if(!IsFinite(value))return 0;return Math.Abs(value)<=1.0000001?value*100.0:value;}
        static double NormalizeVelocity(double value){if(!IsFinite(value)||value<=0)return 170;return value<1.0?1.0/value:value;}
        static double ValueAt(double[] values,int index){return values!=null&&index>=0&&index<values.Length&&IsFinite(values[index])?values[index]:0;}
        static double WindowPeak(double[] values,int center,int before,int after){if(values==null||values.Length==0)return 0;int a=Math.Max(0,center-before),b=Math.Min(values.Length-1,center+after),best=a;for(int i=a+1;i<=b;i++)if(Math.Abs(values[i])>Math.Abs(values[best]))best=i;return values[best];}
        static double WindowCorrelation(double[] values,int aCenter,int bCenter,int before,int after)
        {
            if(values==null||values.Length<2)return 0;int left=Math.Max(0,before),right=Math.Max(0,after),n=left+right+1;double sa=0,sb=0;int count=0;
            for(int k=-left;k<=right;k++){int a=aCenter+k,b=bCenter+k;if(a<0||a>=values.Length||b<0||b>=values.Length)continue;sa+=values[a];sb+=values[b];count++;}
            if(count<2)return 0;double ma=sa/count,mb=sb/count,num=0,da=0,db=0;
            for(int k=-left;k<=right;k++){int a=aCenter+k,b=bCenter+k;if(a<0||a>=values.Length||b<0||b>=values.Length)continue;double x=values[a]-ma,y=values[b]-mb;num+=x*y;da+=x*x;db+=y*y;}
            return da>1e-20&&db>1e-20?Math.Abs(num/Math.Sqrt(da*db)):0;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);ApplyAxisWheel(e,(ModifierKeys&Keys.Shift)==Keys.Shift);
        }
        internal void ApplyAxisWheel(MouseEventArgs e,bool vertical)
        {
            if(e.Delta==0||draggingCursor!=0||boxZoomActive||pulse.Length<2||!PrepareInteraction()||!lastPlot.Contains(e.Location))return;double k=e.Delta>0?.82:1.22;
            if(vertical){double cy=DataY(e.Y,lastPlot),h=(viewY1-viewY0)*k/2;if(!IsFinite(h)||h<1e-12||h>1e100)return;viewY0=cy-h;viewY1=cy+h;}else{double cx=DataX(e.X,lastPlot),h=(viewX1-viewX0)*k/2;viewX0=Math.Max(0,cx-h);viewX1=Math.Min(Math.Max(1,pulse.Length-1),cx+h);if(viewX1-viewX0<2){viewX0=Math.Max(0,cx-1);viewX1=Math.Min(pulse.Length-1,cx+1);}}Invalidate();
        }
        public void SavePng(string filePath,int width,int height){if(string.IsNullOrWhiteSpace(filePath))filePath=Path.Combine(Path.GetTempPath(),"PDmap.png");filePath=Path.GetFullPath(filePath);Directory.CreateDirectory(Path.GetDirectoryName(filePath));using(var b=new Bitmap(Math.Max(360,width),Math.Max(250,height)))using(var g=Graphics.FromImage(b)){Draw(g,new Rectangle(0,0,b.Width,b.Height),false);b.Save(filePath,ImageFormat.Png);}LastOutputPath=filePath;}
        public void ExportDataCsv(string filePath)
        {
            if(string.IsNullOrWhiteSpace(filePath))filePath=Path.Combine(Path.GetTempPath(),"PDmap.csv");
            filePath=Path.GetFullPath(filePath);Directory.CreateDirectory(Path.GetDirectoryName(filePath));
            using(var w=new StreamWriter(filePath,false,new UTF8Encoding(true)))
                WriteExportCsv(w);
            LastOutputPath=filePath;
        }
        void WriteExportCsv(TextWriter w)
        {
            {
                w.WriteLine("PDmap_ID,Parameter_Row,Voltage_kV,Phase_deg,Location_m,RawCharge_pC,CompensatedCharge_pC,TimeDifference_ns,Correlation_percent,Confidence_percent,SameLocationPulseCount");
                w.WriteLine(string.Join(",",currentPdMapId,currentParameterRowIndex,Fmt(voltageKv),Fmt(phaseDeg),Fmt(LocationM),Fmt(rawCharge),Fmt(compensatedCharge),Fmt(TimeDifferenceNs),Fmt(correlation),Fmt(confidence),samePositionPulseCount));
                w.WriteLine();
                w.WriteLine("Index,Time_ns,RawPulse_pC,RawCalibration,RawDamping,FilteredPulse,FilteredCalibration,FilteredDamping,IncidentWindow,ReflectionWindow");
                for(int i=0;i<pulse.Length;i++)
                    w.WriteLine(string.Join(",",i,Fmt(i*dtNs),Fmt(ExportSampleAt(pulse,i,true)),
                        Fmt(ExportSampleAt(alignedCalibration,i,hasAlignedCalibrationCurve)),Fmt(ExportSampleAt(alignedAttenuation,i,HasDampingCurve)),
                        Fmt(ExportSampleAt(filteredPulse,i,true)),Fmt(ExportSampleAt(filteredCalibration,i,hasAlignedCalibrationCurve)),Fmt(ExportSampleAt(filteredAttenuation,i,HasFilteredDampingCurve)),
                        i>=incident-n1&&i<=incident+n2?1:0,ReflectionWithinWaveform&&i>=reflection-n1&&i<=reflection+n2?1:0));
            }
        }
        // Export missing samples as unknown, never as measured zero. Keep this
        // separate from ValueAt so no calculation/display fallback is changed.
        static double ExportSampleAt(double[] values,int index,bool available)
        { return available&&values!=null&&index>=0&&index<values.Length&&IsFinite(values[index])?values[index]:double.NaN; }
        void ExportDataDialog(){using(var d=new SaveFileDialog{Filter="CSV (*.csv)|*.csv",FileName="PDmap.csv"})if(d.ShowDialog(FindForm())==DialogResult.OK)ExportDataCsv(d.FileName);}
        void ExportImageDialog(){using(var d=new SaveFileDialog{Filter="PNG (*.png)|*.png",FileName="PDmap.png"})if(d.ShowDialog(FindForm())==DialogResult.OK)SavePng(d.FileName,Math.Max(1200,Width*2),Math.Max(700,Height*2));}
        Font F(Rectangle c,float px,FontStyle s){return new Font(Font.FontFamily,Font.SizeInPoints,s,GraphicsUnit.Point);}
        static string Fmt(double value)=>value.ToString("R",CultureInfo.InvariantCulture);
        static int Limit(int v,int a,int b)=>v<a?a:v>b?b:v;static double Clamp(double v,double a,double b)=>v<a?a:v>b?b:v;static float ClampF(float v,float a,float b)=>v<a?a:v>b?b:v;static double Min(double[] a){double v=double.MaxValue;foreach(double x in a)if(IsFinite(x)&&x<v)v=x;return v==double.MaxValue?0:v;}static double Max(double[] a){double v=double.MinValue;foreach(double x in a)if(IsFinite(x)&&x>v)v=x;return v==double.MinValue?0:v;}static double NiceMax(double x){if(x<=0)return 1;double p=Math.Pow(10,Math.Floor(Math.Log10(x))),n=x/p;double m=n<=1?1:n<=2?2:n<=3?3:n<=5?5:10;return m*p;}
    }
}
