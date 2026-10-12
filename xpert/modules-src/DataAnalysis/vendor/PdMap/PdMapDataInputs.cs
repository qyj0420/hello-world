using System;
using System.ComponentModel;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        bool showPreviousPositioningResults = false;
        bool previousOverlayExplicit;
        int? pendingPdMapId;

        [Category("Data")]
        [Description("PDmap ID to display. Selects from cached matrices without transferring them again. May be set before LoadDataMatrices.")]
        public int PdMapId
        {
            get { return pendingPdMapId.HasValue ? pendingPdMapId.Value : currentPdMapId; }
            set { SetPdMapId(value); }
        }

        [Category("Data")]
        [Description("Compatibility alias for PdMapId. Selects from cached matrices without transferring them again.")]
        public int SelectedPdMapId
        {
            get { return PdMapId; }
            set { PdMapId=value; }
        }

        [Category("Appearance")]
        [Description("Eight flags: raw, calibration, damping, filtered raw, filtered calibration, filtered damping, joints, boundary. Arrays are cloned.")]
        public bool[] CurveVisibility
        {
            get { return (bool[])curveVisibility.Clone(); }
            set { SetCurveVisibility(value); }
        }

        [Category("Filtering")]
        [Description("Six filter settings; arrays are cloned. Set independently of loaded matrices.")]
        public double[] FilterParameters
        {
            get { return (double[])filterParameters.Clone(); }
            set { SetFilterParameters(value); }
        }

        [Category("Appearance"), DefaultValue(false)]
        [Description("Show the positioning scatter from the positioning-result matrix loaded by LoadDataMatrices. Independent of the eight curve-visibility flags.")]
        public bool ShowPositioningScatter
        {
            get { return showPreviousPositioningResults; }
            set
            {
                if(showPreviousPositioningResults==value)return;
                showPreviousPositioningResults=value;
                BuildPreviousOverlay(); ResetView();
            }
        }

        [Category("Appearance"), DefaultValue(false)]
        [Description("Compatibility alias for ShowPositioningScatter. Legacy data methods may still supply an explicit previous-results matrix.")]
        public bool ShowPreviousPositioningResults
        {
            get { return ShowPositioningScatter; }
            set { ShowPositioningScatter=value; }
        }

        /// <summary>Selects a MAP using cached matrices, or queues the ID until both matrices are loaded.</summary>
        public void SetPdMapId(int pdmapId)
        {
            if(!IsUsableMatrix(positioningMatrix,36)||!IsUsableMatrix(matchingMatrix,1))
            {
                pendingPdMapId=pdmapId;
                return;
            }
            pendingPdMapId=null;
            ApplyIndependentDataInput(null,null,pdmapId);
        }

        public void SetSelectedPdMapId(int pdmapId){SetPdMapId(pdmapId);}
        public void SetShowPositioningScatter(bool visible){ShowPositioningScatter=visible;}
        public void SetShowPreviousPositioningResults(bool visible){ShowPositioningScatter=visible;}

        /// <summary>
        /// LabVIEW two-matrix entry point. All ID, language, filtering and display settings are independent.
        /// No previous-positioning-results input is required; the positioning matrix supplies the scatter.
        /// </summary>
        public void LoadDataMatrices(double[,] positioningResult,double[,] pdmapMatchingMatrix)
        {
            SetDataMatrices(positioningResult,pdmapMatchingMatrix);
        }

        /// <summary>
        /// Loads only the data matrices and retains the independent language, visibility, filter and display settings.
        /// Null reuses that cached matrix. A queued ID is applied first; otherwise a valid current ID is retained,
        /// or the first valid ID is selected. An unavailable queued ID reports LastError after the matrices load.
        /// Positioning overlay uses these positioning results; no previous-results matrix is required.
        /// </summary>
        public void SetDataMatrices(double[,] positioningResult, double[,] pdmapMatchingMatrix)
        {
            if((positioningResult!=null&&!IsUsableMatrix(positioningResult,36)) ||
                (pdmapMatchingMatrix!=null&&!IsUsableMatrix(pdmapMatchingMatrix,1)))
            {
                LastError=L("Data matrices are invalid: positioning results require at least 36 columns and both matrices require nonempty rows.","数据矩阵无效：定位结果至少需要 36 列，两个矩阵均需包含有效行。");
                return;
            }
            double[,] candidate=positioningResult??positioningMatrix;
            if(!IsUsableMatrix(candidate,36)||!IsUsableMatrix(pdmapMatchingMatrix??matchingMatrix,1))
            {
                LastError=L("Load both positioning and matching matrices before selecting a MAP.","选择 MAP 前须先加载定位结果矩阵和匹配图矩阵。");
                return;
            }
            int selected=pendingPdMapId.HasValue?pendingPdMapId.Value:currentPdMapId;
            if(!pendingPdMapId.HasValue&&FindParameterRow(candidate,selected)<0)
            {
                bool found=false;
                for(int r=0;r<candidate.GetLength(0);r++)
                {
                    double id=candidate[r,9];
                    if(!IsFinite(id)||id<int.MinValue||id>int.MaxValue)continue;
                    selected=(int)Math.Round(id); found=true; break;
                }
                if(!found)
                {
                    LastError=L("No finite MAP ID is available in positioning column 9.","定位结果第 9 列（零基）没有有效 MAP 标识。");
                    return;
                }
            }
            pendingPdMapId=null;
            previousOverlayExplicit=false;
            standaloneOverlay=false;
            previousPositioningMatrix=null;
            ApplyIndependentDataInput(positioningResult,pdmapMatchingMatrix,selected);
        }

        void ApplyIndependentDataInput(double[,] positioningResult,double[,] pdmapMatchingMatrix,int pdmapId)
        {
            bool calibrationDisplay=showCalibration, dampingDisplay=showAttenuation;
            try
            {
                SetPdMapData(positioningResult,pdmapMatchingMatrix,pdmapId,null,null,null,null,acceptedPositionErrorPercent,english);
            }
            finally
            {
                showCalibration=calibrationDisplay; showAttenuation=dampingDisplay;
            }
            // SetPdMapData resets the view only when the selected data changes. A repeated
            // two-matrix refresh must preserve manually positioned cursors and the current zoom.
            Invalidate();
        }
    }
}
