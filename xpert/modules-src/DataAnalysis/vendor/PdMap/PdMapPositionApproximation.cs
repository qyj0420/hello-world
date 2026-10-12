using System;
using System.ComponentModel;
using System.Globalization;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        bool approximateCablePositions, approximationApplied, approximationExactEdit;
        bool approximationCommitActive, approximationReleaseCommit;
        double approximationTargetM, approximationErrorRatio;
        string approximationKind = "None", approximationStatusCode = "Disabled";
        ApproximationCandidate approximationCandidate;
        internal event EventHandler ApproximationDisplayChanged;
        internal Func<bool> AutomaticPositionApproximationAllowed;
        // The review owner binds this to a row-aligned, persisted editing flag.
        // A waveform load must not lose an earlier explicit cursor selection.
        internal Func<int,bool> ExactReviewPositionRequired;
        internal Action<int> ExactReviewPositionSelected;
        void NotifyApproximationDisplayChanged(){var h=ApproximationDisplayChanged;if(h!=null)h(this,EventArgs.Empty);}

        // A reversible working snapshot, never the immutable input-MAP snapshot.
        // Geometry refresh reanalyses these indices instead of an already moved cursor.
        sealed class ApproximationCandidate
        {
            internal double[] Waveform;
            internal int MapId, RowIndex, Incident, Reflection, SameCount, BoundaryDirection;
            internal bool MatrixDriven;
            internal double Location, CurrentLocation, TimeDifference, RawCharge;
            internal double CompensatedCharge, Correlation, Confidence, Voltage, Phase;
        }

        [Category("Positioning"), DefaultValue(false)]
        [Description("启用可选的接头及电缆内端点近似；关闭还原该可选近似。电缆外 2% 容差内的位置始终归一到端头，且不移动实测游标。Enable optional joint and in-cable endpoint snapping. Outside positions within the 2% boundary tolerance always project to the endpoint without changing measured cursors.")]
        public bool ApproximateCablePositions
        {
            get { return approximateCablePositions; }
            set
            {
                if(value&&AutomaticPositionApproximationAllowed!=null&&!AutomaticPositionApproximationAllowed()){UpdateDisplayOptions();return;}
                if (approximateCablePositions == value) return;
                if (!value)
                {
                    if (approximationApplied && HasCurrentApproximationCandidate())
                        RestoreApproximationCandidate(true);
                    approximateCablePositions = false;
                    ClearPositionApproximation();
                }
                else
                {
                    approximateCablePositions = true;
                    approximationExactEdit = false;
                    CommitPositionApproximation();
                }
                Invalidate();
                NotifyApproximationDisplayChanged();
                NotifyReviewAnalysisEdited("PositionApproximation");
            }
        }

        public void SetApproximateCablePositions(bool enabled) { ApproximateCablePositions = enabled; }

        [Category("Positioning"), DefaultValue(1D)]
        [Description("允许位置偏差，占电缆长度的百分比；与同位置脉冲统计共用。Position tolerance as a percentage of cable length, shared with same-location pulse statistics.")]
        public double PositionTolerancePercent
        {
            get { return acceptedPositionErrorPercent; }
            set { SetAcceptedPositionErrorPercent(value); }
        }

        public void SetPositionTolerancePercent(double percent) { PositionTolerancePercent = percent; }

        double jointApproximationErrorPercent = 1.0;
        [Category("Positioning"), DefaultValue(1.0)]
        public double JointApproximationErrorPercent
        {
            get { return jointApproximationErrorPercent; }
            set {
                if(!IsFinite(value)||value<0||value>100)throw new ArgumentOutOfRangeException("value");
                if(value==jointApproximationErrorPercent)return;
                jointApproximationErrorPercent=value;
                RefreshPositionApproximationForTargets();NotifyApproximationDisplayChanged();
            }
        }

        [Category("Positioning"),DefaultValue(2.0)]
        [Description("Total accessory approximation band as percent of cable length; 2 means +/-1 percent. Existing PositionTolerancePercent remains the one-sided radius.")]
        public double AccessoryApproximationBandPercent
        {
            get{return acceptedPositionErrorPercent*2;}
            set{if(!IsFinite(value)||value<0||value>200)throw new ArgumentOutOfRangeException("value");SetAcceptedPositionErrorPercent(value/2);}
        }

        [Browsable(false)]
        public double RawLocationM
        {
            get
            {
                double raw=approximationApplied&&approximationCandidate!=null?approximationCandidate.Location:LocationM;
                // Review matrices contain the normalized physical coordinate. Keep
                // the cursor-derived diagnostic accessible without changing timing.
                if(raw==0||raw==cableLengthM)
                {
                    double sample=CalculatedLocation();
                    if(IsFinite(sample)&&sample!=raw&&
                        HVX.SignalEvidence.BoundaryConfidenceMath.ProjectEndpointPosition(sample,cableLengthM)==raw)
                        return sample;
                }
                return raw;
            }
        }

        [Browsable(false)]
        public double DisplayedLocationM
        { get { return HVX.SignalEvidence.BoundaryConfidenceMath.ProjectEndpointPosition(approximationApplied?approximationTargetM:LocationM,cableLengthM); } }

        bool HasBoundaryPositionProjection
        {
            get
            {
                double raw=RawLocationM;
                double projected=HVX.SignalEvidence.BoundaryConfidenceMath.ProjectEndpointPosition(raw,cableLengthM);
                return IsFinite(raw)&&projected!=raw&&DisplayedLocationM==projected;
            }
        }

        [Browsable(false), Description("当前整数游标点计算的位置，与精确附件显示位置区分。Physical location from the current integer cursor indices, distinct from the exact accessory display location.")]
        public double SampleLocationM { get { return CalculatedLocation(); } }

        [Browsable(false)]
        public bool PositionIsApproximated { get { return approximationApplied||HasBoundaryPositionProjection; } }

        [Browsable(false)]
        public string PositionApproximationKindCode { get { return HasBoundaryPositionProjection?"Endpoint":approximationKind; } }

        [Browsable(false)]
        public string PositionApproximationStatusCode { get { return HasBoundaryPositionProjection?"BoundaryToleranceProjection":approximationStatusCode; } }

        [Browsable(false)]
        public int PreApproximationReflectionIndex { get { return approximationApplied ? approximationCandidate.Reflection : CurrentReflectionIndex; } }

        [Browsable(false), Description("有符号近似误差：(附件位置 - 近似前位置) / 电缆长度。Signed correction: (accessory position - pre-approximation location) / cable length.")]
        public double PositionApproximationErrorRatio
        { get { return HasBoundaryPositionProjection?(DisplayedLocationM-RawLocationM)/cableLengthM:approximationApplied?approximationErrorRatio:0; } }

        [Browsable(false)]
        public double PositionApproximationErrorPercent { get { return 100 * PositionApproximationErrorRatio; } }

        [Browsable(false)]
        public string PositionApproximationNote
        {
            get
            {
                if (!PositionIsApproximated) return "";
                string raw = FormatPositionDisplay(PresentPosition(RawLocationM));
                string error = FormatPositionDisplay(reversePositionDisplay?-PositionApproximationErrorPercent:PositionApproximationErrorPercent, true);
                return String.Format(L("Approximated to {0}; raw {1} m; error {2}%","近似至{0}；原始 {1} m；误差 {2}%"),
                    PositionApproximationKindCode=="Endpoint"?L("endpoint","端点"):L("joint","接头"),raw,error);
            }
        }

        static string FormatPositionDisplay(double value, bool signed = false)
        {
            if (!IsFinite(value)) return "--";
            double rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
            if (rounded == 0) rounded = 0; // Do not display a negative zero.
            return rounded.ToString(signed ? "+0.0;-0.0;0.0" : "0.0", CultureInfo.InvariantCulture);
        }

        internal bool IsOutsideCableDisplayRange
        {
            get
            {
                double displayed = DisplayedLocationM;
                return (reflection < incident&&!HasBoundaryPositionProjection) || !IsFinite(displayed) || !IsFinite(cableLengthM) || cableLengthM <= 0 ||
                    displayed < 0 || displayed > cableLengthM;
            }
        }

        // Call before a real user edit, not on mouse-down or paint. Continue from
        // currently visible (possibly approximated) indices, never old candidate.
        void BeginPositionCandidateEdit() { ClearPositionApproximation(); }

        // Load/reset clears bookkeeping only; its caller owns exact new indices.
        void ClearPositionApproximation()
        {
            approximationCandidate = null;
            approximationApplied = false;
            approximationExactEdit = false;
            approximationKind = "None";
            approximationErrorRatio = 0;
            approximationTargetM = double.NaN;
            approximationStatusCode = approximateCablePositions ? "Ready" : "Disabled";
        }

        // Exact +/-1 and restore remain exact until a new edit or off/on toggle.
        // Geometry may update analysis but must not rebound to an attachment.
        void SuppressPositionApproximationForExactEdit()
        {
            ClearPositionApproximation();
            approximationExactEdit = true;
            approximationStatusCode = approximateCablePositions ? "ExactEdit" : "Disabled";
            if(matrixDriven&&reviewLoadDepth==0&&currentParameterRowIndex>=0&&ExactReviewPositionSelected!=null)
                ExactReviewPositionSelected(currentParameterRowIndex);
        }

        bool HasCurrentApproximationCandidate()
        {
            return approximationCandidate != null && ReferenceEquals(approximationCandidate.Waveform, pulse) &&
                approximationCandidate.MapId == currentPdMapId && approximationCandidate.RowIndex == currentParameterRowIndex &&
                approximationCandidate.MatrixDriven == matrixDriven && approximationCandidate.Incident >= 0 &&
                approximationCandidate.Reflection >= 0 && approximationCandidate.Incident < pulse.Length &&
                approximationCandidate.Reflection < pulse.Length;
        }

        ApproximationCandidate CaptureApproximationCandidate()
        {
            return new ApproximationCandidate {
                Waveform = pulse, MapId = currentPdMapId, RowIndex = currentParameterRowIndex,
                MatrixDriven = matrixDriven, Incident = incident, Reflection = reflection,
                Location = LocationM, CurrentLocation = currentLocationM, TimeDifference = currentTimeDifferenceNs,
                RawCharge = rawCharge, CompensatedCharge = compensatedCharge, Correlation = correlation,
                Confidence = confidence, Voltage = voltageKv, Phase = phaseDeg,
                SameCount = samePositionPulseCount, BoundaryDirection = boundaryDirection
            };
        }

        void RestoreApproximationCandidate(bool restoreAnalysis)
        {
            ApproximationCandidate candidate = approximationCandidate;
            incident = candidate.Incident;
            reflection = candidate.Reflection;
            approximationApplied = false;
            AlignCalibration();
            RecomputeFilteredCurves();
            // Project calibration alignment may compute a compensation value.
            // Restore input analysis last so disabling remains exactly reversible.
            if (restoreAnalysis)
            {
                currentLocationM = candidate.CurrentLocation;
                currentTimeDifferenceNs = candidate.TimeDifference;
                rawCharge = candidate.RawCharge;
                compensatedCharge = candidate.CompensatedCharge;
                correlation = candidate.Correlation;
                confidence = candidate.Confidence;
                voltageKv = candidate.Voltage;
                phaseDeg = candidate.Phase;
                samePositionPulseCount = candidate.SameCount;
                boundaryDirection = candidate.BoundaryDirection;
            }
            ValidateReflectionSelectionWindow();
        }

        // After the operation's peak selection and analysis. Never call from
        // UpdateDynamicAnalysis: getters/paints are pure and snaps cannot recurse.
        void CommitPositionApproximation()
        {
            if(AutomaticPositionApproximationAllowed!=null&&!AutomaticPositionApproximationAllowed()){approximationStatusCode="ManualLearningMode";return;}
            if(!reflectionSelected){approximationStatusCode="ReflectionNotSelected";return;}
            // Outside positions within the shared 2% boundary band are real
            // endpoint coordinates, even with optional joint snapping disabled.
            // This correction never shifts the measured I/R indices or delay.
            if(HasBoundaryPositionProjection)
            {
                approximationStatusCode="BoundaryToleranceProjection";
                CountSamePositionPulses(DisplayedLocationM);
                return;
            }
            // Method 7's endpoint identity is a coordinate projection backed by
            // CAL morphology, never permission to rewrite measured I/R/delay.
            if(SeventhEndpointTimingMustBePreserved()){approximationStatusCode="SeventhEndpointTimingPreserved";return;}
            if (approximationCommitActive) return;
            if (!approximateCablePositions) { approximationStatusCode = "Disabled"; return; }
            if (approximationExactEdit || matrixDriven && ExactReviewPositionRequired!=null && ExactReviewPositionRequired(currentParameterRowIndex))
            { approximationStatusCode = "ExactEdit"; return; }
            // LabVIEW may write geometry/option ports while the pointer is held.
            // Reanalyse those inputs, but keep the preview at the user's sample
            // until the actual mouse-up commit (capture loss is not a release).
            if (draggingCursor != 0 && !approximationReleaseCommit)
            { approximationStatusCode = "DeferredUntilRelease"; return; }
            // An unchanged duplicate commit is a no-op; real edits call Begin first.
            if (approximationApplied && HasCurrentApproximationCandidate()) return;
            approximationCommitActive = true;
            try
            {
                approximationApplied = false;
                approximationKind = "None";
                approximationErrorRatio = 0;
                approximationCandidate = CaptureApproximationCandidate();
                double raw = approximationCandidate.Location;
                if (pulse.Length == 0 || incident < 0 || incident >= pulse.Length || reflection < 0 || reflection >= pulse.Length)
                { approximationStatusCode = "NoWaveform"; return; }
                if (reflection < incident) { approximationStatusCode = "ReflectionBeforeIncident"; return; }
                if (!IsFinite(raw) || !IsFinite(cableLengthM) || cableLengthM <= 0 ||
                    !IsFinite(acceptedPositionErrorPercent) || acceptedPositionErrorPercent < 0)
                { approximationStatusCode = "InvalidGeometry"; return; }
                double tolerance = cableLengthM * (acceptedPositionErrorPercent / 100.0);
                if (!IsFinite(tolerance)) { approximationStatusCode = "InvalidGeometry"; return; }

                double nearest = double.PositiveInfinity, target = raw;
                string kind = "None";
                ConsiderApproximationCandidate(raw, 0, "Endpoint", tolerance, ref nearest, ref target, ref kind);
                ConsiderApproximationCandidate(raw, cableLengthM, "Endpoint", tolerance, ref nearest, ref target, ref kind);
                if (connectors != null)
                    foreach (double joint in connectors)
                        if (IsFinite(joint) && joint >= 0 && joint <= cableLengthM)
                            ConsiderApproximationCandidate(raw, joint, "Joint", cableLengthM*jointApproximationErrorPercent/100.0, ref nearest, ref target, ref kind);
                if (kind == "None") { approximationStatusCode = "OutsideTolerance"; return; }

                double delta;
                if (!TrySampleDeltaForPosition(target, out delta) || !IsFinite(delta) || delta < 0)
                { approximationStatusCode = "MappingUnavailable"; return; }
                double continuousIndex = incident + delta;
                // Never clamp inaccessible accessories then claim success. Check
                // the continuous location AND its nearest integer sample index.
                if (!IsFinite(continuousIndex) || continuousIndex < incident || continuousIndex > pulse.Length - 1)
                { approximationStatusCode = "TargetOutsideWaveform"; return; }
                double roundedIndex = Math.Round(continuousIndex, MidpointRounding.AwayFromZero);
                if (roundedIndex < incident || roundedIndex > pulse.Length - 1)
                { approximationStatusCode = "TargetOutsideWaveform"; return; }
                int targetIndex = (int)roundedIndex;
                int firstAllowed,lastAllowed;
                if(!TryReflectionSelectionBounds(out firstAllowed,out lastAllowed)||targetIndex<firstAllowed||targetIndex>lastAllowed)
                { approximationStatusCode = "TargetOutsideReflectionWindow"; return; }
                if (target == raw && targetIndex == reflection)
                { approximationStatusCode = "ExactMatch"; return; }

                reflection = targetIndex;
                if (reflection != approximationCandidate.Reflection)
                {
                    AlignCalibration();
                    RecomputeFilteredCurves();
                    UpdateDynamicAnalysis();
                    if (!matrixDriven) CountSamePositionPulses(LocationM);
                }
                approximationTargetM = target;
                approximationErrorRatio = (target - raw) / cableLengthM;
                approximationKind = kind;
                approximationApplied = true;
                approximationStatusCode = "Applied";
                // Statistics follow the exact displayed accessory, not a possible
                // half-sample displacement introduced by integer cursor rounding.
                CountSamePositionPulses(target);
            }
            finally
            {
                approximationCommitActive = false;
                Invalidate();
            }
        }

        void CommitPositionApproximationOnRelease()
        {
            approximationReleaseCommit = true;
            try { CommitPositionApproximation(); }
            finally { approximationReleaseCommit = false; }
        }

        void RetainDraggingPositionCandidate()
        {
            if (draggingCursor == 0) return;
            // A press on an already snapped marker has not necessarily produced
            // a mouse-move yet. Do not restore its older, unsnapped indices when
            // an external port refresh arrives between mouse-down and mouse-up.
            approximationApplied = false;
            approximationCandidate = null;
            approximationKind = "None";
            approximationErrorRatio = 0;
        }

        // After length/speed/mirror/tolerance/joint/mixed-material changes. Restore
        // candidate indices only: the newly supplied geometry belongs to the caller.
        void RefreshPositionApproximationForGeometry()
        {
            if (approximationCommitActive) return;
            RetainDraggingPositionCandidate();
            if (HasCurrentApproximationCandidate() && !approximationExactEdit)
                RestoreApproximationCandidate(false);
            approximationApplied = false;
            approximationCandidate = null;
            approximationKind = "None";
            approximationErrorRatio = 0;
            UpdateDynamicAnalysis();
            if (!matrixDriven) CountSamePositionPulses(LocationM);
            CommitPositionApproximation();
            Invalidate();
        }

        // Tolerance changes do not change timing or waveform evidence. Retain the
        // candidate's input analysis instead of replacing it with a fresh estimate.
        void RefreshPositionApproximationForTargets()
        {
            if (approximationCommitActive) return;
            RetainDraggingPositionCandidate();
            if (HasCurrentApproximationCandidate() && !approximationExactEdit)
                RestoreApproximationCandidate(true);
            approximationApplied = false;
            approximationCandidate = null;
            approximationKind = "None";
            approximationErrorRatio = 0;
            ValidateReflectionSelectionWindow();
            CountSamePositionPulses(LocationM);
            CommitPositionApproximation();
            Invalidate();
        }

        static void ConsiderApproximationCandidate(double raw, double reference, string referenceKind, double tolerance,
            ref double nearest, ref double candidate, ref string candidateKind)
        {
            double distance = Math.Abs(raw - reference);
            // Compare against interval endpoints, not a subtract-then-compare:
            // e.g. 0.4 - 0.3 can exceed the representable 0.1 by one ULP.
            if (!IsFinite(distance) || raw < reference - tolerance || raw > reference + tolerance) return;
            // Endpoints considered first; equal-distance joints never displace them.
            // Equal-kind ties choose the lower physical coordinate deterministically.
            bool tiePreferred = distance == nearest && candidateKind == referenceKind && reference < candidate;
            if (distance < nearest || tiePreferred)
            {
                nearest = distance;
                candidate = reference;
                candidateKind = referenceKind;
            }
        }
    }
}
