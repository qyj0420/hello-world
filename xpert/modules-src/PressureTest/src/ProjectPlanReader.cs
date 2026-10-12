using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace HVX.Controls
{
    /// <summary>
    /// Reads only pressure-test fields from ITEM.in. Unknown project fields are
    /// ignored to preserve compatibility with old and new project revisions.
    /// </summary>
    internal static class ProjectPlanReader
    {
        private static readonly Regex NumberPattern = new Regex(
            @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private enum FieldKind
        {
            Unknown,
            PhaseVoltage,
            Multipliers,
            RepetitionCounts,
            PlanRow
        }

        internal static ProjectPressurePlan Read(string itemPath)
        {
            if (string.IsNullOrWhiteSpace(itemPath))
            {
                throw new ArgumentException("ITEM.in path is required.", "itemPath");
            }

            return Parse(DecodeText(File.ReadAllBytes(itemPath)));
        }

        internal static bool TryRead(
            string itemPath,
            out ProjectPressurePlan plan,
            out string error)
        {
            try
            {
                plan = Read(itemPath);
                if (!plan.IsValid)
                {
                    error = plan.Warnings.Count > 0
                        ? string.Join("; ", plan.Warnings)
                        : "ITEM.in does not contain a valid pressure plan.";
                    return false;
                }

                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                plan = new ProjectPressurePlan(
                    double.NaN,
                    new List<PressurePlanRow>(),
                    new List<string> { ex.Message });
                error = ex.Message;
                return false;
            }
        }

        internal static ProjectPressurePlan Parse(string content)
        {
            List<string> warnings = new List<string>();
            List<double> multipliers = new List<double>();
            List<int> counts = new List<int>();
            List<PressurePlanRow> explicitRows = new List<PressurePlanRow>();
            double phaseVoltageKv = double.NaN;
            FieldKind pendingField = FieldKind.Unknown;

            if (string.IsNullOrWhiteSpace(content))
            {
                warnings.Add("ITEM.in is empty.");
                return new ProjectPressurePlan(phaseVoltageKv, explicitRows, warnings);
            }

            string[] lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            // The explicit mode takes precedence over legacy placeholder rows.
            // Never turn Free value into a one-step automatic pressure plan.
            string declaredMode = null;
            foreach (string raw in lines)
            {
                string modeKey, modeValue;
                if (!TrySplitKeyValue(StripComment(raw).Trim(), out modeKey, out modeValue)) continue;
                string key = NormalizeKey(modeKey);
                if (key != "加压模式" && key != "pressuremode" && key != "testmode") continue;
                string value = CleanFieldValue(modeValue).Trim();
                if (declaredMode != null && !String.Equals(declaredMode, value, StringComparison.OrdinalIgnoreCase)) warnings.Add("Conflicting pressure modes in ITEM.in.");
                declaredMode = value;
            }
            bool freeValue = String.Equals(declaredMode, "任意值", StringComparison.Ordinal) ||
                String.Equals(declaredMode, "Free value", StringComparison.OrdinalIgnoreCase) || String.Equals(declaredMode, "Arbitrary", StringComparison.OrdinalIgnoreCase);
            int lineIndex;
            for (lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                string line = StripComment(lines[lineIndex]).Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                string key;
                string value;
                bool hasSeparator = TrySplitKeyValue(line, out key, out value);
                FieldKind kind = hasSeparator ? ClassifyKey(key) : FieldKind.Unknown;
                if (freeValue && kind != FieldKind.Unknown && kind != FieldKind.PhaseVoltage)
                { pendingField = FieldKind.Unknown; continue; }

                if (kind != FieldKind.Unknown)
                {
                    if (pendingField != FieldKind.Unknown)
                        warnings.Add("Incomplete pressure-plan field before ITEM.in line " + (lineIndex + 1) + ".");
                    string fieldValue = CleanFieldValue(value);
                    List<double> numbers = ParseFieldNumbers(kind, fieldValue, warnings, lineIndex + 1);
                    if (numbers.Count == 0)
                    {
                        pendingField = fieldValue.Length == 0 ? kind : FieldKind.Unknown;
                        continue;
                    }

                    ApplyField(
                        kind,
                        numbers,
                        ref phaseVoltageKv,
                        multipliers,
                        counts,
                        explicitRows,
                        warnings,
                        lineIndex + 1);
                    pendingField = FieldKind.Unknown;
                    continue;
                }

                if (!hasSeparator)
                {
                    kind = ClassifyKey(line);
                    if (kind != FieldKind.Unknown && !(freeValue && kind != FieldKind.PhaseVoltage))
                    {
                        pendingField = kind;
                        continue;
                    }
                }

                List<double> looseNumbers = ParseNumbers(hasSeparator ? value : line);
                if (pendingField != FieldKind.Unknown && !hasSeparator)
                {
                    looseNumbers = ParseFieldNumbers(pendingField, CleanFieldValue(line), warnings, lineIndex + 1);
                    ApplyField(
                        pendingField,
                        looseNumbers,
                        ref phaseVoltageKv,
                        multipliers,
                        counts,
                        explicitRows,
                        warnings,
                        lineIndex + 1);
                    pendingField = FieldKind.Unknown;
                    continue;
                }

                // ITEM.in also contains indexed measurement records such as
                // `0 = "ALL,200000,..."`.  Those are not pressure-plan rows;
                // only separator-free CSV rows are accepted as the compact
                // plan-row form.
                if (!freeValue && !hasSeparator && LooksLikePlanRow(line, looseNumbers))
                {
                    looseNumbers = ParseFieldNumbers(FieldKind.PlanRow, CleanFieldValue(line), warnings, lineIndex + 1);
                    AddExplicitRow(explicitRows, looseNumbers, warnings, lineIndex + 1);
                }
            }

            if (pendingField != FieldKind.Unknown)
                warnings.Add("Incomplete pressure-plan field at the end of ITEM.in.");

            if (!IsFinite(phaseVoltageKv) || phaseVoltageKv <= 0.0)
            {
                warnings.Add("ITEM.in is missing a positive phase voltage (kV).");
            }

            if (freeValue) return new ProjectPressurePlan(phaseVoltageKv, new List<PressurePlanRow>(), warnings, true);
            if (explicitRows.Count > 0 && (multipliers.Count > 0 || counts.Count > 0))
                warnings.Add("ITEM.in contains conflicting array and row pressure plans.");
            List<PressurePlanRow> rows = explicitRows.Count > 0
                ? NormalizeExplicitRows(explicitRows, phaseVoltageKv, warnings)
                : BuildRows(phaseVoltageKv, multipliers, counts, warnings);

            if (rows.Count == 0)
            {
                warnings.Add("ITEM.in does not contain a usable pressure-plan row.");
            }

            return new ProjectPressurePlan(phaseVoltageKv, rows, warnings);
        }

        private static void ApplyField(
            FieldKind kind,
            IList<double> numbers,
            ref double phaseVoltageKv,
            IList<double> multipliers,
            IList<int> counts,
            IList<PressurePlanRow> rows,
            IList<string> warnings,
            int lineNumber)
        {
            int i;
            if (kind == FieldKind.PhaseVoltage)
            {
                double value = numbers[0];
                if (IsFinite(value) && value > 0.0)
                {
                    phaseVoltageKv = value;
                }
                else
                {
                    warnings.Add("Invalid phase voltage at ITEM.in line " + lineNumber + ".");
                }

                return;
            }

            if (kind == FieldKind.Multipliers)
            {
                for (i = 0; i < numbers.Count; i++)
                {
                    if (IsFinite(numbers[i]) && numbers[i] >= 0.0)
                    {
                        multipliers.Add(numbers[i]);
                    }
                    else
                    {
                        warnings.Add("Invalid pressure multiplier at ITEM.in line " + lineNumber + ".");
                    }
                }

                return;
            }

            if (kind == FieldKind.RepetitionCounts)
            {
                for (i = 0; i < numbers.Count; i++)
                {
                    int count = ToPositiveInteger(numbers[i]);
                    if (count > 0)
                    {
                        counts.Add(count);
                    }
                    else
                    {
                        warnings.Add("Invalid repetition count at ITEM.in line " + lineNumber + ".");
                    }
                }

                return;
            }

            if (kind == FieldKind.PlanRow)
            {
                AddExplicitRow(rows, numbers, warnings, lineNumber);
            }
        }

        private static List<PressurePlanRow> BuildRows(
            double phaseVoltageKv,
            IList<double> multipliers,
            IList<int> counts,
            IList<string> warnings)
        {
            List<PressurePlanRow> rows = new List<PressurePlanRow>();
            if (multipliers.Count == 0)
            {
                return rows;
            }

            if (counts.Count != multipliers.Count)
            {
                warnings.Add("Pressure multiplier/count lengths must match exactly.");
                return rows;
            }

            int i;
            for (i = 0; i < multipliers.Count; i++)
            {
                int repetitions = counts[i];
                double voltageKv = IsFinite(phaseVoltageKv)
                    ? phaseVoltageKv * multipliers[i]
                    : double.NaN;
                if (!IsFinite(voltageKv) || voltageKv < 0.0)
                    warnings.Add("Invalid calculated RMS voltage for plan row " + (i + 1) + ".");
                rows.Add(new PressurePlanRow(i + 1, multipliers[i], repetitions, voltageKv));
            }

            return rows;
        }

        private static List<PressurePlanRow> NormalizeExplicitRows(
            IList<PressurePlanRow> source,
            double phaseVoltageKv,
            IList<string> warnings)
        {
            List<PressurePlanRow> rows = new List<PressurePlanRow>(source.Count);
            int i;
            for (i = 0; i < source.Count; i++)
            {
                PressurePlanRow row = source[i];
                // Project-editor calculated voltage is always U0 multiplier times
                // phase RMS voltage. Never interpret it as an already converted peak.
                double voltageKv = phaseVoltageKv * row.U0Multiplier;
                if (!IsFinite(voltageKv) || voltageKv < 0.0)
                    warnings.Add("Unable to determine RMS voltage for plan row " + (i + 1) + ".");
                if (!double.IsNaN(row.VoltageKv) && (!IsFinite(row.VoltageKv) || row.VoltageKv < 0.0 ||
                    Math.Abs(row.VoltageKv - voltageKv) > 1e-8 * Math.Max(1.0, Math.Abs(voltageKv))))
                    warnings.Add("Plan row " + (i + 1) + " voltage does not equal phase RMS voltage times multiplier.");

                rows.Add(new PressurePlanRow(
                    i + 1,
                    row.U0Multiplier,
                    row.RepetitionCount,
                    voltageKv));
            }

            return rows;
        }

        private static void AddExplicitRow(
            IList<PressurePlanRow> rows,
            IList<double> numbers,
            IList<string> warnings,
            int lineNumber)
        {
            int offset = numbers.Count == 4 ? 1 : 0;
            if (numbers.Count < 2 || numbers.Count > 4)
            {
                warnings.Add("Incomplete plan row at ITEM.in line " + lineNumber + ".");
                return;
            }

            double multiplier = numbers[offset];
            int repetitions = ToPositiveInteger(numbers[offset + 1]);
            double voltageKv = numbers.Count - offset >= 3
                ? numbers[offset + 2]
                : double.NaN;
            if (!IsFinite(multiplier) || multiplier < 0.0 || repetitions <= 0)
            {
                warnings.Add("Invalid plan row at ITEM.in line " + lineNumber + ".");
                return;
            }

            rows.Add(new PressurePlanRow(rows.Count + 1, multiplier, repetitions, voltageKv));
        }

        private static FieldKind ClassifyKey(string key)
        {
            string normalized = NormalizeKey(key);
            if (ContainsAny(
                normalized,
                "加压倍数",
                "u0倍数",
                "uo倍数",
                "voltagemultiplier",
                "voltagefactor",
                "pressuremultiplier"))
            {
                return FieldKind.Multipliers;
            }

            if (ContainsAny(
                normalized,
                "加压次数",
                "重复次数",
                "试验次数",
                "repetitioncount",
                "repeatcount",
                "pressurecount",
                "pressurerepetitions"))
            {
                return FieldKind.RepetitionCounts;
            }

            if (ContainsAny(
                normalized,
                "加压计划",
                "计划行",
                "加压步骤",
                "planrow",
                "pressurestep"))
            {
                return FieldKind.PlanRow;
            }

            if (ContainsAny(
                normalized,
                "相电压",
                "额定相电压",
                "phasevoltage",
                "u0电压",
                "uo电压") ||
                normalized == "u0" ||
                normalized == "uo" ||
                normalized == "u0kv" ||
                normalized == "uokv")
            {
                return FieldKind.PhaseVoltage;
            }

            return FieldKind.Unknown;
        }

        private static bool ContainsAny(string value, params string[] candidates)
        {
            int i;
            for (i = 0; i < candidates.Length; i++)
            {
                if (value.IndexOf(candidates[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeKey(string key)
        {
            if (key == null)
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(key.Length);
            int i;
            for (i = 0; i < key.Length; i++)
            {
                char character = key[i];
                if (char.IsLetterOrDigit(character) ||
                    (character >= '\u4e00' && character <= '\u9fff'))
                {
                    builder.Append(char.ToLowerInvariant(character));
                }
            }

            return builder.ToString();
        }

        private static bool TrySplitKeyValue(string line, out string key, out string value)
        {
            int separatorIndex = -1;
            char[] separators = { '=', ':', '：', '\t' };
            int i;
            for (i = 0; i < separators.Length; i++)
            {
                int candidate = line.IndexOf(separators[i]);
                if (candidate >= 0 && (separatorIndex < 0 || candidate < separatorIndex))
                {
                    separatorIndex = candidate;
                }
            }

            if (separatorIndex < 0)
            {
                key = line;
                value = string.Empty;
                return false;
            }

            key = line.Substring(0, separatorIndex).Trim();
            value = line.Substring(separatorIndex + 1).Trim();
            return true;
        }

        private static List<double> ParseNumbers(string value)
        {
            List<double> numbers = new List<double>();
            if (string.IsNullOrWhiteSpace(value))
            {
                return numbers;
            }

            MatchCollection matches = NumberPattern.Matches(value);
            int i;
            for (i = 0; i < matches.Count; i++)
            {
                double number;
                if (double.TryParse(
                    matches[i].Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out number))
                {
                    numbers.Add(number);
                }
            }

            return numbers;
        }

        private static string CleanFieldValue(string value)
        {
            string text = (value ?? string.Empty).Trim();
            while (text.StartsWith("=", StringComparison.Ordinal) || text.StartsWith(":", StringComparison.Ordinal) || text.StartsWith("：", StringComparison.Ordinal))
                text = text.Substring(1).TrimStart();
            if (text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"')
                text = text.Substring(1, text.Length - 2).Trim();
            return text;
        }

        private static List<double> ParseFieldNumbers(FieldKind kind, string value, IList<string> warnings, int lineNumber)
        {
            if (value.Length == 0) return new List<double>();
            string text = value;
            if (kind == FieldKind.PhaseVoltage)
                text = Regex.Replace(text, @"\s*k[vV]\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();
            string[] tokens = text.IndexOfAny(new[] { ',', '，', ';', '；', '|' }) >= 0
                ? text.Split(new[] { ',', '，', ';', '；', '|' }, StringSplitOptions.None)
                : Regex.Split(text, @"\s+", RegexOptions.CultureInvariant);
            List<double> numbers = new List<double>();
            foreach (string token in tokens)
            {
                double number;
                if (token.Trim().Length == 0 || !double.TryParse(token.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) || !IsFinite(number))
                {
                    warnings.Add("Invalid pressure-plan numeric field at ITEM.in line " + lineNumber + ".");
                    return new List<double>();
                }
                numbers.Add(number);
            }
            if (kind == FieldKind.PhaseVoltage && numbers.Count != 1)
            {
                warnings.Add("Phase voltage must contain one RMS value at ITEM.in line " + lineNumber + ".");
                numbers.Clear();
            }
            return numbers;
        }

        private static bool LooksLikePlanRow(string line, IList<double> numbers)
        {
            if (numbers == null || numbers.Count < 2)
            {
                return false;
            }

            return line.IndexOf(',') >= 0 ||
                line.IndexOf('，') >= 0 ||
                line.IndexOf('\t') >= 0 ||
                line.IndexOf('|') >= 0;
        }

        private static int ToPositiveInteger(double value)
        {
            if (!IsFinite(value) || value < 1.0 || value > int.MaxValue)
            {
                return -1;
            }

            double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
            return Math.Abs(value - rounded) <= 1e-6 ? (int)rounded : -1;
        }

        private static string StripComment(string line)
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("#", StringComparison.Ordinal) ||
                trimmed.StartsWith(";", StringComparison.Ordinal) ||
                trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            int hash = line.IndexOf('#');
            int slash = line.IndexOf("//", StringComparison.Ordinal);
            int cut = hash;
            if (slash >= 0 && (cut < 0 || slash < cut))
            {
                cut = slash;
            }

            return cut >= 0 ? line.Substring(0, cut) : line;
        }

        private static string DecodeText(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            if (bytes.Length >= 3 &&
                bytes[0] == 0xEF &&
                bytes[1] == 0xBB &&
                bytes[2] == 0xBF)
            {
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }

            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.GetEncoding(936).GetString(bytes);
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
