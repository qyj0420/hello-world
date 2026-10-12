using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using LabVIEWPdMap.ProjectCalibrationImplementation;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        string projectPath = "", projectCatalogStatus = "Disabled", projectSelectionStatus = "Disabled";
        List<Candidate> projectCatalog = new List<Candidate>();
        Candidate selectedProjectCalibration, projectedAlignmentCalibration;
        double[] legacyCalibration = Array.Empty<double>(), legacyAttenuation = Array.Empty<double>();
        int legacyCalibrationPeak, projectCatalogGeneration, selectedCatalogGeneration = -1, selectedPhysicalPhase = -1;
        double selectedTargetLength = double.NaN;
        double projectBaseline, projectIncidentAmplitude, projectReflectionAmplitude, projectDecaySlopePerNs = double.NaN;
        int projectIncidentPeak, projectReflectionPeak, projectedIncident = -1;
        double projectedDtNs = double.NaN, projectAlignedDurationNs;
        double[] projectedPulse;
        bool projectedAlignmentReady;
        string projectCalibrationDetails = "Disabled";

        bool ProjectCalibrationEnabled { get { return !string.IsNullOrWhiteSpace(projectPath); } }

        [Category("Data"), DefaultValue("")]
        [Description("Project directory or ITEM.in path. Loads root CAL and historical Calibration/Live archives once; phase and length come from the selected positioning row. Empty uses legacy calibration inputs.")]
        public string ProjectPath
        {
            get { return projectPath; }
            set
            {
                string next = (value ?? "").Trim();
                if (string.Equals(projectPath, next, StringComparison.OrdinalIgnoreCase)) return;
                projectPath = next;
                ReloadProjectCalibration();
            }
        }

        public void SetProjectPath(string value) { ProjectPath = value; }

        [Browsable(false)] public string SelectedCalibrationPath { get { return selectedProjectCalibration == null ? "" : selectedProjectCalibration.Path; } }
        [Browsable(false)] public string ProjectCalibrationStatus
        {
            get { return FormatProjectCalibrationStatus(); }
            private set { projectCalibrationDetails = value ?? ""; }
        }
        [Browsable(false)] public string ProjectCalibrationStatusCode
        { get { return projectCalibrationDetails.Split(new[] { ':', ';' }, 2)[0].Trim(); } }
        [Browsable(false)] public string ProjectCalibrationDetails { get { return projectCalibrationDetails; } }
        [Browsable(false)] public bool CalibrationLoading { get; private set; }
        [Browsable(false)] public int ProjectCalibrationCandidateCount { get { return projectCatalog.Count; } }
        [Browsable(false)] public int ProjectCalibrationReadCount { get; private set; }
        [Browsable(false)] public double SelectedCalibrationSampleRateHz { get { return selectedProjectCalibration == null ? double.NaN : selectedProjectCalibration.Rate; } }
        [Browsable(false)] public int SelectedCalibrationRangeIndex { get { return selectedProjectCalibration == null ? -1 : selectedProjectCalibration.RangeIndex; } }
        [Browsable(false)] public string SelectedCalibrationTimingMode { get { return selectedProjectCalibration == null || !selectedProjectCalibration.IsLiveArchive ? "Unspecified" : selectedProjectCalibration.FirstArrival ? "FirstArrival" : "Peak"; } }
        [Browsable(false)] public string ProjectCalibrationDisplayMode { get { return hasAlignedCalibrationCurve && ProjectCalibrationEnabled ? "DisplayNormalized" : "Unavailable"; } }
        [Browsable(false)] public int SelectedCalibrationIncidentPeakIndex { get { return selectedProjectCalibration == null ? -1 : projectIncidentPeak; } }
        [Browsable(false)] public int SelectedCalibrationReflectionPeakIndex { get { return selectedProjectCalibration == null ? -1 : projectReflectionPeak; } }
        [Browsable(false)] public double ProjectDampingSlopePerNs { get { return projectDecaySlopePerNs; } }

        string FormatProjectCalibrationStatus()
        {
            string code = ProjectCalibrationStatusCode, text;
            switch (code)
            {
                case "Disabled": text = L("Project calibration is disabled; using the supplied calibration inputs.","项目校准未启用，使用现有校准输入。"); break;
                case "WaitingForPositioningRow": text = L("Project calibration is loaded; waiting for a valid positioning row.","项目校准已读取，等待有效定位结果行。"); break;
                case "SelectedExactPhase": text = L("Selected calibration for the same physical phase.","已选择与当前定位结果同相的校准文件。"); break;
                case "SelectedAllFallback": text = L("No valid same-phase calibration; selected the ALL calibration.","本相无有效校准，已选择 ALL 通用校准。"); break;
                case "SelectedNearestOtherPhase": text = L("Selected another phase's calibration with the nearest CAL cable length.","本相及 ALL 均无有效校准，已按 CAL 电缆长度最接近原则选择其他相校准。"); break;
                case "NoCalibrationFiles": text = L("The project has no root CAL or historical live calibration files.","项目中没有根目录 CAL 或历史实机校准文件。"); break;
                case "NoValidCalibration": text = L("No CAL file passed the waveform, timing and header checks.","没有 CAL 文件通过波形、时标与表头校验。"); break;
                case "NoAllCalibration": text = L("An ALL positioning row requires a valid ALL calibration.","ALL 定位结果只能使用有效的 ALL 校准文件。"); break;
                case "InvalidTestPhase": text = L("Positioning column 0 must be 0 (ALL), 1 (A), 2 (B), or 3 (C).","定位结果第 0 列须为整数 0（ALL）、1（A）、2（B）或 3（C）。"); break;
                case "MissingCableLengthForCrossPhaseFallback": text = L("The positioning row has no valid cable length for cross-phase calibration selection.","当前定位结果行缺少有效电缆长度，无法跨相选择校准。"); break;
                case "InvalidProjectPath": text = L("The project directory or ITEM.in path is invalid or does not exist.","项目目录或 ITEM.in 路径无效或不存在。"); break;
                case "ProjectReadError": text = L("The project calibration files could not be read. See ProjectCalibrationDetails.","无法读取项目校准文件，详情见 ProjectCalibrationDetails。"); break;
                case "TooManyCalibrationFiles": text = L("The project exceeds the limit of 256 top-level CAL files.","项目顶层 CAL 文件超过 256 个读取上限。"); break;
                case "CalibrationCatalogTooLarge": text = L("The CAL catalog exceeds the total limit of 128 MiB.","项目 CAL 文件总大小超过 128 MiB 读取上限。"); break;
                default: text = L("Project calibration is unavailable. See ProjectCalibrationDetails.","项目校准不可用，详情见 ProjectCalibrationDetails。"); break;
            }
            if (projectCalibrationDetails.Contains("AlignmentUnavailable"))
                text += L(" Alignment is unavailable because the incident amplitude is zero, nonfinite or overflows.","当前入射幅值为零、非有限值或对齐幅值溢出，无法对齐。");
            else if (projectCalibrationDetails.Contains("DampingAvailable:"))
                text += L(" A decreasing damping curve is fitted from the CAL pulse amplitude ratio and time separation.","已依据 CAL 双脉冲幅值比及时间间隔拟合下降阻尼曲线。");
            else if (projectCalibrationDetails.Contains("DampingUnavailable:"))
                text += projectCalibrationDetails.Contains("raw_reference=")?
                    (L(" Raw CAL does not qualify as an attenuation reference (reflection at least 5% and reliable pulse evidence required); calibration remains viewable.","原始 CAL 未通过衰减参考校验（反射至少为入射的 5%，且须有可靠脉冲证据）；仍可查看校准波形。")):
                    (L(" The CAL data does not support a finite decreasing fit or a visible damping interval.","CAL 数据不支持有限下降拟合，或阻尼区间超出可见采样范围。"));
            return text + " [" + code + "]";
        }

        /// <summary>Explicitly rereads the project CAL catalog. Display/filter/cursor operations never read project files.</summary>
        public void ReloadProjectCalibration()
        {
            ReadProjectMetadataCatalog();
            projectCatalogGeneration++;
            projectCatalog = new List<Candidate>();
            ClearSelectedProjectCalibration();
            if (!ProjectCalibrationEnabled)
            {
                projectCatalogStatus = projectSelectionStatus = ProjectCalibrationStatus = "Disabled";
                calibration = legacyCalibration; attenuation = legacyAttenuation; calibrationPeak = legacyCalibrationPeak;
                if(matrixDriven)compensatedCharge=Safe(originalParameterRow,16,rawCharge);
                ApplyProjectMetadataForMap(matrixDriven?originalParameterRow:null);
            }
            else
            {
                // Clear the prior project's data before any potentially failing I/O.
                calibration = attenuation = Array.Empty<double>();
                CalibrationLoading = true; ProjectCalibrationReadCount++;
                try
                {
                    string normalized;
                    projectCatalog = Selector.ReadCatalog(projectPath, out normalized);
                    projectCatalogStatus = "CatalogLoaded";
                }
                catch (SelectionFailure ex) { projectCatalogStatus = ex.Code + ": " + ex.Message; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is System.Security.SecurityException)
                { projectCatalogStatus = "ProjectReadError: " + ex.Message; }
                finally { CalibrationLoading = false; }
                SelectProjectCalibrationForMap(matrixDriven ? originalParameterRow : null);
            }
            // The legacy zero-curve shortcut must not retain a former project overlay.
            alignmentPulse = alignmentCalibration = alignmentAttenuation = null;
            AlignCalibration(); RecomputeFilteredCurves();
            RefreshCableStructureAnalysis();
            RememberLoadedInputs(); viewReady = false; Invalidate();
        }

        void CaptureLegacyCalibration()
        {
            legacyCalibration = calibration; legacyAttenuation = attenuation; legacyCalibrationPeak = calibrationPeak;
        }

        void ClearSelectedProjectCalibration()
        {
            selectedProjectCalibration = null; selectedCatalogGeneration = -1;
            selectedPhysicalPhase = -1; selectedTargetLength = double.NaN;
            projectDecaySlopePerNs = double.NaN; projectAlignedDurationNs = 0;
            projectedAlignmentReady = false; projectedAlignmentCalibration = null;
            projectSelectionStatus = "WaitingForPositioningRow";
        }

        void SelectProjectCalibrationForMap(double[] row)
        {
            ApplyProjectMetadataForMap(row);
            if (!ProjectCalibrationEnabled) return;
            if (projectCatalogStatus != "CatalogLoaded")
            {
                ClearSelectedProjectCalibration(); calibration = attenuation = Array.Empty<double>();
                ProjectCalibrationStatus = projectCatalogStatus; return;
            }
            double code = Safe(row, 0, double.NaN), length = CableLengthSource=="ExistingValue"?double.NaN:CalibrationSelectionTotalLength(cableLengthM);
            if (row == null || row.Length == 0 || !IsFinite(code) || code < 0 || code > 3 || code != Math.Round(code))
            {
                ClearSelectedProjectCalibration(); calibration = attenuation = Array.Empty<double>();
                projectSelectionStatus = row == null || row.Length == 0 ? "WaitingForPositioningRow" : "InvalidTestPhase";
                ProjectCalibrationStatus = projectSelectionStatus; return;
            }
            int phase = (int)code;
            if (selectedCatalogGeneration == projectCatalogGeneration && selectedPhysicalPhase == phase && selectedTargetLength.Equals(length))
            {
                calibration = selectedProjectCalibration == null ? Array.Empty<double>() : selectedProjectCalibration.Wave;
                attenuation = Array.Empty<double>(); return;
            }
            ClearSelectedProjectCalibration();
            selectedCatalogGeneration = projectCatalogGeneration; selectedPhysicalPhase = phase; selectedTargetLength = length;
            try
            {
                selectedProjectCalibration = Selector.SelectForMap(projectCatalog, phase, length, out projectSelectionStatus);
                calibration = selectedProjectCalibration.Wave; attenuation = Array.Empty<double>();
                PrepareProjectCalibrationPeaks(); calibrationPeak = projectIncidentPeak;
                ProjectCalibrationStatus = projectSelectionStatus;
            }
            catch (SelectionFailure ex)
            {
                selectedProjectCalibration = null; calibration = attenuation = Array.Empty<double>();
                projectSelectionStatus = ex.Code + ": " + ex.Message;
                if (ex.Code == "NoValidCalibration")
                    projectSelectionStatus += " Candidates: " + string.Join("; ", projectCatalog.Select(c => Path.GetFileName(c.Path) + "=" + c.Reason));
                ProjectCalibrationStatus = projectSelectionStatus;
            }
        }

        void PrepareProjectCalibrationPeaks()
        {
            Candidate c = selectedProjectCalibration;
            double[] sorted = (double[])c.Wave.Clone(); Array.Sort(sorted);
            int middle = sorted.Length / 2;
            projectBaseline = sorted.Length % 2 == 0 ? sorted[middle - 1] / 2 + sorted[middle] / 2 : sorted[middle];
            int radius = c.HasMeasuredReflection ? Math.Max(2, Math.Min(64, (c.Reflection - c.Incident) / 8)) : 64;
            projectIncidentPeak = c.IsLiveArchive ? c.IncidentPeak : ProjectPeakIndex(c.Wave, Math.Max(0, c.Incident - radius), Math.Min(c.Wave.Length - 1, c.HasMeasuredReflection ? Math.Min(c.Reflection - 1, c.Incident + radius) : c.Incident + radius), projectBaseline);
            projectIncidentAmplitude = c.Wave[projectIncidentPeak] - projectBaseline;
            // An incident-only electrical calibration stays viewable. It cannot
            // invent a reflection, propagation interval or attenuation fit.
            if (!c.HasMeasuredReflection)
            {
                projectReflectionPeak = -1; projectReflectionAmplitude = double.NaN;
                projectAlignedDurationNs = 0; projectDecaySlopePerNs = double.NaN;
                rawDampingReferenceReason = "no_measured_calibration_reflection"; return;
            }
            projectReflectionPeak = c.IsLiveArchive ? c.ReflectionPeak : ProjectPeakIndex(c.Wave, Math.Max(projectIncidentPeak + 1, c.Reflection - radius), Math.Min(c.Wave.Length - 1, c.Reflection + radius), projectBaseline);
            projectReflectionAmplitude = c.Wave[projectReflectionPeak] - projectBaseline;
            projectAlignedDurationNs = (projectReflectionPeak - (double)projectIncidentPeak) * 1e9 / c.Rate;
            double ratioLog = Math.Log(Math.Abs(projectReflectionAmplitude)) - Math.Log(Math.Abs(projectIncidentAmplitude));
            double slope = ratioLog / projectAlignedDurationNs;
            var reference=CachedRawCalibrationReference(c.Wave,c.Incident,c.Reflection,c.Rate);
            rawDampingReferenceReason=reference.Reason;
            projectDecaySlopePerNs = reference.Available && IsFinite(slope) && slope < 0 && projectAlignedDurationNs > 0 ? slope : double.NaN;
        }

        static int ProjectPeakIndex(double[] values, int start, int end, double baseline)
        {
            int best = start;
            for (int i = start + 1; i <= end; i++)
                if (Math.Abs(values[i] - baseline) > Math.Abs(values[best] - baseline)) best = i;
            return best;
        }

        void AlignProjectCalibration()
        {
            int displayIncident = DisplayIncidentPeak();
            // Alignment depends on the CAL's own sampling rate, never on the PD reflection cursor.
            bool same = projectedAlignmentReady && ReferenceEquals(projectedPulse, pulse) &&
                ReferenceEquals(projectedAlignmentCalibration, selectedProjectCalibration) && projectedIncident == displayIncident && projectedDtNs.Equals(dtNs);
            if (same) { UpdateProjectCompensation(); return; }
            projectedAlignmentReady = true; projectedPulse = pulse; projectedAlignmentCalibration = selectedProjectCalibration;
            projectedIncident = displayIncident; projectedDtNs = dtNs;
            alignedCalibration = new double[pulse.Length]; alignedAttenuation = new double[pulse.Length];
            hasAlignedCalibrationCurve = hasDampingCurve = false;
            if (selectedProjectCalibration == null || pulse.Length == 0 || incident < 0 || incident >= pulse.Length || !IsFinite(dtNs) || dtNs <= 0)
            { UpdateProjectCompensation(); return; }
            double amplitude = pulse[displayIncident], scale = amplitude / projectIncidentAmplitude;
            if (!IsFinite(amplitude) || Math.Abs(amplitude) <= 1e-12 || !IsFinite(scale))
            {
                ProjectCalibrationStatus = projectSelectionStatus + "; AlignmentUnavailable: zero/nonfinite incident amplitude";
                UpdateProjectCompensation(); return;
            }
            Candidate c = selectedProjectCalibration;
            // Display normalization only: never apply the saved pC/V factor to
            // CAL volts or mutate its samples. Separate real sample rates map
            // elapsed time to the plot; waveform point counts do not stretch it.
            double calSamplesPerPulseSample = dtNs * c.Rate / 1e9;
            for (int i = 0; i < pulse.Length; i++)
            {
                double source = projectIncidentPeak + (i - (double)displayIncident) * calSamplesPerPulseSample;
                if (!IsFinite(source) || source < 0 || source > c.Wave.Length - 1) continue;
                int left = (int)Math.Floor(source), right = Math.Min(left + 1, c.Wave.Length - 1);
                double fraction = source - left;
                double sample = (1 - fraction) * (c.Wave[left] - projectBaseline) + fraction * (c.Wave[right] - projectBaseline);
                double value = sample * scale;
                if (!IsFinite(value))
                {
                    Array.Clear(alignedCalibration, 0, alignedCalibration.Length);
                    ProjectCalibrationStatus = projectSelectionStatus + "; AlignmentUnavailable: amplitude overflow";
                    UpdateProjectCompensation(); return;
                }
                alignedCalibration[i] = value;
            }
            hasAlignedCalibrationCurve = HasNonzeroFiniteCurve(alignedCalibration);
            if (hasAlignedCalibrationCurve && IsFinite(projectDecaySlopePerNs))
            {
                double end = Math.Min(pulse.Length - 1, displayIncident + projectAlignedDurationNs / dtNs);
                for (int i = displayIncident; i <= end; i++)
                    alignedAttenuation[i] = amplitude * Math.Exp(projectDecaySlopePerNs * (i - (double)displayIncident) * dtNs);
                hasDampingCurve = end >= displayIncident + 1 && HasNonzeroFiniteCurve(alignedAttenuation);
                if (!hasDampingCurve) Array.Clear(alignedAttenuation, 0, alignedAttenuation.Length);
            }
            HVX.SignalEvidence.SixthCalibrationReference rawReference;
            bool rawQualified=TryRawDampingReference(out rawReference);
            ProjectCalibrationStatus = projectSelectionStatus + "; DisplayNormalized: raw_CAL_units=V; sample_rate_hz=" + c.Rate.ToString("R", CultureInfo.InvariantCulture) +
                "; capture_range_index=" + c.RangeIndex.ToString(CultureInfo.InvariantCulture) + "; timing_mode=" + SelectedCalibrationTimingMode +
                (hasDampingCurve ? "; DampingAvailable: qualified raw CAL peak ratio/time fit" :
                !rawQualified?"; DampingUnavailable: raw_reference="+rawDampingReferenceReason:
                "; DampingUnavailable: CAL does not support a finite decreasing fit or visible interval");
            UpdateProjectCompensation();
        }

        void UpdateProjectCompensation()
        {
            if (!ProjectCalibrationEnabled || !matrixDriven) return;
            if(!reflectionSelected){compensatedCharge=double.NaN;return;}
            compensatedCharge = rawCharge;
            if (!hasAlignedCalibrationCurve || !IsFinite(projectDecaySlopePerNs) || reflection < incident || !IsFinite(rawCharge)) return;
            double exponent = -projectDecaySlopePerNs * (reflection - (double)incident) * dtNs;
            double value = rawCharge * Math.Exp(exponent);
            if (IsFinite(value)) compensatedCharge = Math.Round(value);
        }
    }
}
