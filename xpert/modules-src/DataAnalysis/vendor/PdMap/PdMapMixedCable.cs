using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LabVIEWPdMap
{
    public sealed partial class PdMapControl
    {
        // Task17 AstraProjectInput/AstraBoundaryOutput contract: fixed project
        // coordinates run from 0 to L. Normal timing integrates FROM L;
        // the existing MirrorLocation contract integrates FROM 0. Material
        // intervals are never reordered, and their physical coordinates do not change.
        bool mixedCableCalculation, mixedCableModelValid;
        double[] mixedBoundaries = Array.Empty<double>(), mixedVelocities = Array.Empty<double>();
        double[] mixedForwardTimesUs = Array.Empty<double>(), mixedReverseTimesUs = Array.Empty<double>();
        string[] mixedVelocitySources = Array.Empty<string>();
        double mixedOneWayUs;
        string mixedModelCode = "NotConfigured", mixedModelDetails = "";

        [Category("Positioning"), DefaultValue(false)]
        [Description("按当前各段材料的已配置波速计算混合电缆位置。Calculate mixed-cable positions using configured segment velocities.")]
        public bool MixedCableCalculation
        {
            get { return mixedCableCalculation || connectingCableLengthM>0; }
            set
            {
                if (mixedCableCalculation == value) return;
                mixedCableCalculation = value;
                RefreshCableStructureAnalysis();
            }
        }

        public void SetMixedCableCalculation(bool enabled) { MixedCableCalculation = enabled; }
        [Browsable(false)] public bool MixedCableModelValid { get { return mixedCableModelValid; } }
        [Browsable(false)] public bool MixedCableCalculationActive { get { return MixedCableCalculation && mixedCableModelValid; } }
        [Browsable(false)] public string MixedCableStatusCode { get { return MixedCableCalculation ? mixedModelCode : "Disabled"; } }
        [Browsable(false)] public string MixedCableDetails { get { return mixedModelDetails; } }
        [Browsable(false)] public double[] ResolvedSegmentBoundariesM { get { return (double[])mixedBoundaries.Clone(); } }
        [Browsable(false)] public double[] ResolvedSegmentVelocitiesMPerUs { get { return (double[])mixedVelocities.Clone(); } }
        [Browsable(false)] public string[] ResolvedSegmentVelocitySources { get { return (string[])mixedVelocitySources.Clone(); } }
        [Browsable(false)] public double MixedCableRoundTripSamples
        {
            get { return mixedCableModelValid && IsFinite(dtNs) && dtNs > 0 ? 2000.0 * mixedOneWayUs / dtNs : double.NaN; }
        }
        [Browsable(false)] public string MixedCableStatus
        {
            get
            {
                if (!MixedCableCalculation) return L("Mixed-cable calculation is disabled.","混合电缆计算未启用。");
                string result;
                if (!mixedCableModelValid)
                    result = L("Mixed-cable model is unavailable; no location is calculated.","混合电缆模型不可用，暂不计算定位位置。");
                else if (mixedModelCode == "ReadyConfiguredUniform")
                    result = L("All segments use the explicitly configured uniform velocity; no material-specific velocity is available.","各段使用已明确配置的统一波速；未取得材料专属波速。");
                else if (mixedModelCode == "ReadyWithUniformFallback")
                    result = L("Configured material velocities are used; remaining segments use the explicitly configured uniform velocity.","已使用材料配置波速；其余段使用已明确配置的统一波速。");
                else
                    result = L("Using configured material velocities for every segment.","各段均使用材料已配置波速。");
                return result + " [" + mixedModelCode + "]";
            }
        }

        // Called only after input/model changes. No disk access or parsing is
        // performed by PositionForSampleDelta/TrySampleDeltaForPosition/paint.
        void RefreshMixedCableModel()
        {
            mixedCableModelValid = false;
            mixedBoundaries = mixedVelocities = Array.Empty<double>();
            mixedForwardTimesUs = mixedReverseTimesUs = Array.Empty<double>();
            mixedVelocitySources = Array.Empty<string>();
            mixedOneWayUs = 0;
            mixedModelCode = "InvalidGeometry"; mixedModelDetails = "";
            if(ApplyReviewConnectingMixedModel())return;
            int count = currentMaterialOriginalNames == null ? 0 : currentMaterialOriginalNames.Length;
            if (!IsFinite(cableLengthM) || cableLengthM <= 0 || count < 1 || count > 4096 ||
                currentMaterialStartsM == null || currentMaterialEndsM == null ||
                currentMaterialStartsM.Length != count || currentMaterialEndsM.Length != count)
            { mixedModelDetails = "Material intervals must completely cover the current cable."; return; }

            double epsilon = 64.0 * 2.2204460492503131e-16 * Math.Max(1, cableLengthM);
            var boundaries = new double[count + 1]; var velocities = new double[count]; var sources = new string[count];
            var projectByMaterial = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var invalidProjectMaterials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (projectMaterialOriginalNames != null)
                foreach (string material in projectMaterialOriginalNames)
                {
                    double value; string key;
                    if (!TryMaterialVelocity(material, out value, out key))
                    {
                        // A plain material override cannot turn an explicitly wrong
                        // project velocity into an apparently valid uniform fallback.
                        // Only an unambiguous material prefix before a named speed
                        // declaration is recognized; never infer a material from suffixes.
                        if (TryInvalidProjectMaterialKey(material, out key)) invalidProjectMaterials.Add(key);
                        continue;
                    }
                    if (key.Length == 0) continue;
                    double previous;
                    if (projectByMaterial.TryGetValue(key, out previous) && !SameMixedVelocity(previous, value)) conflicts.Add(key);
                    else projectByMaterial[key] = value;
                }

            int fallbackCount = 0;
            for (int i = 0; i < count; i++)
            {
                double start = currentMaterialStartsM[i], end = currentMaterialEndsM[i];
                if (!IsFinite(start) || !IsFinite(end) || end <= start || start < -epsilon || end > cableLengthM + epsilon ||
                    Math.Abs(start - (i == 0 ? 0 : boundaries[i])) > epsilon)
                { mixedModelDetails = "Material intervals must be ordered, continuous and non-overlapping."; return; }
                boundaries[i] = i == 0 ? 0 : boundaries[i]; boundaries[i + 1] = end;
                string text = currentMaterialOriginalNames[i] ?? "", key;
                double speed;
                if (TryMaterialVelocity(text, out speed, out key)) sources[i] = "MaterialExplicit";
                else
                {
                    if (HasMaterialVelocityDeclaration(text))
                    { mixedModelCode = "InvalidMaterialVelocity"; mixedModelDetails = "Invalid velocity declaration in segment " + (i + 1).ToString(CultureInfo.InvariantCulture) + "."; return; }
                    key = MixedMaterialKey(text);
                    if (invalidProjectMaterials.Contains(key))
                    { mixedModelCode = "InvalidProjectMaterialVelocity"; mixedModelDetails = "Invalid same-phase project velocity for material: " + key + ". Supply an explicit valid segment velocity to override it."; return; }
                    if (conflicts.Contains(key))
                    { mixedModelCode = "AmbiguousMaterialVelocity"; mixedModelDetails = "Conflicting same-phase configured velocities for material: " + key; return; }
                    if (key.Length > 0 && projectByMaterial.TryGetValue(key, out speed)) sources[i] = "SamePhaseProjectMaterial";
                    else if (velocityConfigured && IsFinite(velocityMPerUs) && velocityMPerUs > 0)
                    { speed = velocityMPerUs; sources[i] = "ConfiguredUniform"; fallbackCount++; }
                    else
                    { mixedModelCode = "MissingMaterialVelocity"; mixedModelDetails = "No configured velocity for segment " + (i + 1).ToString(CultureInfo.InvariantCulture) + "."; return; }
                }
                velocities[i] = speed;
                mixedOneWayUs += (end - start) / speed;
                if (!IsFinite(mixedOneWayUs) || mixedOneWayUs <= 0)
                { mixedModelCode = "InvalidMaterialVelocity"; mixedModelDetails = "Segment travel time is not finite and positive."; mixedOneWayUs = 0; return; }
            }
            if (Math.Abs(boundaries[count] - cableLengthM) > epsilon)
            { mixedModelDetails = "Material intervals do not reach the cable end."; mixedOneWayUs = 0; return; }
            boundaries[count] = cableLengthM;
            var forward = new double[count + 1]; var reverse = new double[count + 1];
            for (int i = 0; i < count; i++) forward[i + 1] = forward[i] + (boundaries[i + 1] - boundaries[i]) / velocities[i];
            for (int i = count - 1; i >= 0; i--) reverse[i] = reverse[i + 1] + (boundaries[i + 1] - boundaries[i]) / velocities[i];
            mixedOneWayUs = forward[count]; reverse[0] = mixedOneWayUs;
            mixedForwardTimesUs = forward; mixedReverseTimesUs = reverse;
            mixedBoundaries = boundaries; mixedVelocities = velocities; mixedVelocitySources = sources;
            mixedCableModelValid = true;
            mixedModelCode = fallbackCount == count ? "ReadyConfiguredUniform" : fallbackCount > 0 ? "ReadyWithUniformFallback" : "ReadyMaterialVelocities";
            mixedModelDetails = "Segments=" + count.ToString(CultureInfo.InvariantCulture) + "; configured uniform fallback=" + fallbackCount.ToString(CultureInfo.InvariantCulture);
        }

        // deltaSamples is signed (reflection minus incident). Outside-cable
        // results remain signed; use the endpoint's speed for extrapolation.
        double PositionForSampleDelta(double deltaSamples)
        {
            if (!IsFinite(deltaSamples) || !IsFinite(dtNs) || dtNs <= 0) return double.NaN;
            if (!MixedCableCalculation)
            {
                double travel = deltaSamples * dtNs * velocityMPerUs / 2000.0;
                return mirrorLocation ? travel : cableLengthM - travel;
            }
            if (!mixedCableModelValid) return double.NaN;
            double timeUs = deltaSamples * dtNs / 2000.0, position;
            if (!IsFinite(timeUs)) return double.NaN;
            int last = mixedVelocities.Length - 1;
            if (timeUs < 0) position = mirrorLocation ? timeUs * mixedVelocities[0] : cableLengthM - timeUs * mixedVelocities[last];
            else if (timeUs > mixedOneWayUs) position = mirrorLocation ? cableLengthM + (timeUs - mixedOneWayUs) * mixedVelocities[last] : -(timeUs - mixedOneWayUs) * mixedVelocities[0];
            else
            {
                // Cached cumulative travel times make each lookup O(log segments).
                // Drawing N material intervals therefore never repeats N full integrations.
                int lo = 0, hi = last;
                while (lo < hi)
                {
                    int mid = lo + (hi - lo) / 2;
                    bool inLower = mirrorLocation ? timeUs <= mixedForwardTimesUs[mid + 1] : timeUs >= mixedReverseTimesUs[mid + 1];
                    if (inLower) hi = mid; else lo = mid + 1;
                }
                position = mirrorLocation ? mixedBoundaries[lo] + (timeUs - mixedForwardTimesUs[lo]) * mixedVelocities[lo] :
                    mixedBoundaries[lo + 1] - (timeUs - mixedReverseTimesUs[lo + 1]) * mixedVelocities[lo];
                double epsilon = 64.0 * 2.2204460492503131e-16 * Math.Max(1, cableLengthM) * mixedVelocities.Length;
                if (Math.Abs(position) <= epsilon) position = 0;
                if (Math.Abs(position - cableLengthM) <= epsilon) position = cableLengthM;
            }
            return position;
        }

        // Exact inverse, in sample deltas. Do not round at a joint: rounding is
        // only for an actual integer cursor index, never for plotted geometry.
        bool TrySampleDeltaForPosition(double positionM, out double deltaSamples)
        {
            deltaSamples = double.NaN;
            if (!IsFinite(positionM) || !IsFinite(dtNs) || dtNs <= 0) return false;
            if (!MixedCableCalculation)
            {
                if (!IsFinite(velocityMPerUs) || velocityMPerUs <= 0) return false;
                deltaSamples = (mirrorLocation ? positionM : cableLengthM - positionM) * 2000.0 / velocityMPerUs / dtNs;
                return IsFinite(deltaSamples);
            }
            if (!mixedCableModelValid) return false;
            double remaining;
            if (positionM < 0) remaining = mirrorLocation ? positionM / mixedVelocities[0] : mixedOneWayUs - positionM / mixedVelocities[0];
            else if (positionM > cableLengthM) remaining = mirrorLocation ? mixedOneWayUs + (positionM - cableLengthM) / mixedVelocities[mixedVelocities.Length - 1] : (cableLengthM - positionM) / mixedVelocities[mixedVelocities.Length - 1];
            else
            {
                int lo = 0, hi = mixedVelocities.Length - 1;
                while (lo < hi)
                {
                    int mid = lo + (hi - lo) / 2;
                    if (positionM <= mixedBoundaries[mid + 1]) hi = mid; else lo = mid + 1;
                }
                remaining = mirrorLocation ? mixedForwardTimesUs[lo] + (positionM - mixedBoundaries[lo]) / mixedVelocities[lo] :
                    mixedReverseTimesUs[lo + 1] + (mixedBoundaries[lo + 1] - positionM) / mixedVelocities[lo];
            }
            deltaSamples = remaining * 2000.0 / dtNs;
            return IsFinite(deltaSamples);
        }

        static bool SameMixedVelocity(double a, double b)
        { return Math.Abs(a - b) <= 1e-10 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b))); }

        const string MixedNumeric = @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?";
        const string MixedUnit = @"m\s*/\s*(?:usec|us|μs|µs|s|nsec|ns)|米\s*/\s*(?:微秒|秒|纳秒)";
        static double MixedUnitScale(string unit)
        {
            unit = Regex.Replace(unit ?? "", @"\s+", "").ToLowerInvariant();
            if (unit == "m/s" || unit == "米/秒") return 1e-6;
            if (unit == "m/ns" || unit == "m/nsec" || unit == "米/纳秒") return 1000;
            if (unit.Length == 0 || unit == "m/us" || unit == "m/usec" || unit == "m/μs" || unit == "m/µs" || unit == "米/微秒") return 1;
            return double.NaN;
        }
        static bool HasMaterialVelocityDeclaration(string text)
        { return Regex.IsMatch(text ?? "", @"velocity|波速|\bv\s*(?:\(|[:=：])|m\s*/|米\s*/", RegexOptions.IgnoreCase); }

        static bool TryInvalidProjectMaterialKey(string text, out string materialKey)
        {
            materialKey = "";
            text = (text ?? "").Replace('（', '(').Replace('）', ')');
            Match declaration = Regex.Match(text, @"(?:\bvelocity\b|波速|\bv\b)\s*(?:\([^)]*\))?\s*[:=：\t]", RegexOptions.IgnoreCase);
            if (!declaration.Success || declaration.Index == 0) return false;
            string prefix = text.Substring(0, declaration.Index).Trim(' ', '\t', ';', '；', ',', '(', ')');
            if (prefix.Length == 0 || HasMaterialVelocityDeclaration(prefix)) return false;
            materialKey = MixedMaterialKey(prefix);
            return materialKey.Length > 0;
        }

        static bool TryMaterialVelocity(string text, out double velocity, out string materialKey)
        {
            velocity = double.NaN; materialKey = "";
            text = (text ?? "").Replace('（', '(').Replace('）', ')');
            string tail = @"(?=$|[\s,;；)m米])\s*(?:(?<after>" + MixedUnit + @")(?![A-Za-z]))?";
            Match named = Regex.Match(text, @"(?:\bvelocity\b|波速|\bv\b)\s*(?:\((?<before>[^)]+)\))?\s*[:=：\t]\s*(?<number>" + MixedNumeric + ")" + tail, RegexOptions.IgnoreCase);
            Match prefix = Regex.Match(text, @"(?<![\w/])(?<before>" + MixedUnit + @")\s*[:=：\t]\s*(?<number>" + MixedNumeric + ")" + tail, RegexOptions.IgnoreCase);
            Match match = named.Success ? named : prefix.Success ? prefix : Regex.Match(text, @"(?<![\w.])(?<number>" + MixedNumeric + @")\s*(?<after>" + MixedUnit + @")(?![A-Za-z])", RegexOptions.IgnoreCase);
            if (!match.Success) return false;
            if (!match.Groups["after"].Success && Regex.IsMatch(text.Substring(match.Index + match.Length), @"^\s*[A-Za-zμµ米]+\s*/")) return false;
            double number;
            if (!double.TryParse(match.Groups["number"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) || !IsFinite(number) || number <= 0) return false;
            double before = MixedUnitScale(match.Groups["before"].Value), after = MixedUnitScale(match.Groups["after"].Value);
            if (!IsFinite(before) || !IsFinite(after) || match.Groups["before"].Success && match.Groups["after"].Success && before != after) return false;
            velocity = number * (match.Groups["after"].Success ? after : before);
            string material = (text.Substring(0, match.Index) + " " + text.Substring(match.Index + match.Length)).Trim(' ', '\t', ';', '；', ',', '(', ')');
            // Multiple declarations are ambiguous, even if the first is valid.
            if (HasMaterialVelocityDeclaration(material)) { velocity = double.NaN; return false; }
            materialKey = MixedMaterialKey(material);
            return IsFinite(velocity) && velocity > 0;
        }

        static string MixedMaterialKey(string material)
        { return Regex.Replace(ProjectMaterialEnglishName((material ?? "").Trim()), @"[\s_\-‐‑–—]+", "").ToUpperInvariant(); }
    }
}
