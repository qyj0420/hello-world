using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HVX.Task17.Location
{
    /// <summary>
    /// Filename-derived acquisition times, separate from the unchanged Task17 36-column matrix.
    /// Time table columns: source row, PDmap ID, location-data ID, local yyyyMMddHHmmss,
    /// UTC Unix seconds, explicit UTC offset minutes. Unknown time values are NaN.
    /// This class never reads file contents, file modification times or the machine time zone.
    /// </summary>
    public sealed class LocationMeasurementTimeBuilder
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly Regex NamePattern = new Regex(
            @"^(ALL|A|B|C)_([-+]?[0-9]+(?:\.[0-9]+)?)kV_(?<stamp>[0-9]{14}|[0-9]{4}[-_][0-9]{2}[-_][0-9]{2}[-_][0-9]{2}[':_-][0-9]{2}[':_-][0-9]{2})$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public LocationMeasurementTimeBuilder() { }

        /// <summary>
        /// Each source path must correspond exactly to its parameter row. Paths need not exist.
        /// An explicit offset of 480 means UTC+08:00; NaN preserves local time with unknown UTC.
        /// False means a structural input error and returns an empty table. True may contain
        /// NaN times for unparseable rows; statusMessage reports completeness and per-row reasons.
        /// </summary>
        public bool BuildMeasurementTimeTable(double[,] parameterMatrix, string[] sourceFilePathsByRow,
            double sourceUtcOffsetMinutes, out double[,] timeTable, out string statusMessage)
        {
            timeTable = new double[0, 6];
            statusMessage = "";
            try
            {
                int rows = ValidateParameters(parameterMatrix);
                ValidateOffset(sourceUtcOffsetMinutes);
                if ((sourceFilePathsByRow == null ? 0 : sourceFilePathsByRow.Length) != rows)
                    throw new ArgumentException("SourcePathRowCountMismatch: provide one source filename per parameter row.");
                double[,] result = NewTable(parameterMatrix, rows);
                var diagnostics = new List<string>();
                for (int r = 0; r < rows; r++)
                {
                    string code = SetTime(result, r, parameterMatrix, sourceFilePathsByRow[r], sourceUtcOffsetMinutes);
                    if (code != "ok") diagnostics.Add(RowDiagnostic(r, code));
                }
                timeTable = result;
                statusMessage = Success(result, 0, diagnostics);
                return true;
            }
            catch (ArgumentException ex)
            {
                statusMessage = Failure(ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Append times for one newly analyzed LOC batch. Existing time values are copied exactly.
        /// Historical rows must remain the same ordered prefix, with matching map and location IDs.
        /// After manually reordering/filtering/reindexing history, rebuild from explicit row paths.
        /// </summary>
        public bool AppendMeasurementTimeTable(double[,] totalParameterMatrix, double[,] previousTimeTable,
            string currentLocationFilePath, double sourceUtcOffsetMinutes,
            out double[,] totalTimeTable, out string statusMessage)
        {
            totalTimeTable = new double[0, 6];
            statusMessage = "";
            try
            {
                int rows = ValidateParameters(totalParameterMatrix);
                ValidateOffset(sourceUtcOffsetMinutes);
                int previousRows = previousTimeTable == null ? 0 : previousTimeTable.GetLength(0);
                if (previousTimeTable != null && previousTimeTable.GetLength(1) != 6)
                    throw new ArgumentException("HistorySchemaMismatch: the time table must have exactly six columns.");
                if (previousRows > rows)
                    throw new ArgumentException("HistoryRowCountMismatch: history cannot exceed the total matrix.");
                double[,] result = NewTable(totalParameterMatrix, rows);
                var diagnostics = new List<string>();
                for (int r = 0; r < previousRows; r++)
                {
                    if (previousTimeTable[r, 0] != r || previousTimeTable[r, 1] != totalParameterMatrix[r, 9] ||
                        previousTimeTable[r, 2] != totalParameterMatrix[r, 1])
                        throw new ArgumentException("HistoryIdentityMismatch: time row " + r.ToString(Inv) +
                            " does not match the ordered parameter prefix; rebuild from original per-row source paths.");
                    ValidateHistoricalTime(previousTimeTable, r);
                    for (int c = 3; c < 6; c++) result[r, c] = previousTimeTable[r, c];
                    if (double.IsNaN(result[r, 3])) diagnostics.Add(RowDiagnostic(r, "historical_time_unknown"));
                    else if (double.IsNaN(result[r, 4])) diagnostics.Add(RowDiagnostic(r, "historical_local_time_only"));
                }
                for (int r = previousRows; r < rows; r++)
                {
                    if (totalParameterMatrix[r, 1] != totalParameterMatrix[previousRows, 1])
                        throw new ArgumentException("CurrentBatchIdentityMismatch: the newly appended rows must belong to one location-data ID.");
                    string code = SetTime(result, r, totalParameterMatrix, currentLocationFilePath, sourceUtcOffsetMinutes);
                    if (code != "ok") diagnostics.Add(RowDiagnostic(r, code));
                }
                totalTimeTable = result;
                statusMessage = Success(result, previousRows, diagnostics);
                return true;
            }
            catch (ArgumentException ex)
            {
                statusMessage = Failure(ex.Message);
                return false;
            }
        }

        private static int ValidateParameters(double[,] matrix)
        {
            if (matrix == null) return 0;
            int rows = matrix.GetLength(0), cols = matrix.GetLength(1);
            if (cols < 36 && !(rows == 0 && cols == 0))
                throw new ArgumentException("ParameterSchemaMismatch: the parameter matrix requires at least 36 columns.");
            for (int r = 0; r < rows; r++)
                if (!ValidId(matrix[r, 1]) || !ValidId(matrix[r, 9]))
                    throw new ArgumentException("InvalidRowIdentity: row " + r.ToString(Inv) +
                        " requires finite nonnegative exact integer location and PDmap IDs.");
            return rows;
        }

        private static bool ValidId(double value)
        { return Finite(value) && value >= 0 && value <= 9007199254740991d && value == Math.Floor(value); }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }

        private static void ValidateOffset(double value)
        {
            if (!double.IsNaN(value) && (!Finite(value) || value < -840 || value > 840 || value != Math.Floor(value)))
                throw new ArgumentException("InvalidUtcOffset: use whole minutes in [-840,840], or NaN when unknown.");
        }

        private static double[,] NewTable(double[,] parameters, int rows)
        {
            var result = new double[rows, 6];
            for (int r = 0; r < rows; r++)
            {
                result[r, 0] = r; result[r, 1] = parameters[r, 9]; result[r, 2] = parameters[r, 1];
                for (int c = 3; c < 6; c++) result[r, c] = double.NaN;
            }
            return result;
        }

        private static string SetTime(double[,] output, int row, double[,] parameters, string path, double offset)
        {
            if (string.IsNullOrWhiteSpace(path)) return "missing_source_path";
            if (path.Length > 32768 || path.IndexOf('\0') >= 0) return "invalid_source_path";
            // Both separators are accepted independently of the host OS. No file-system access.
            string name = path.Replace('\\', '/');
            name = name.Substring(name.LastIndexOf('/') + 1);
            if (name.EndsWith(".loc", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dem", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);
            Match match = NamePattern.Match(name);
            if (!match.Success) return "unsupported_filename_format";
            double voltage;
            if (!double.TryParse(match.Groups[2].Value, NumberStyles.Float, Inv, out voltage) || !Finite(voltage) || voltage < 0)
                return "invalid_filename_voltage";
            string phase = match.Groups[1].Value.ToUpperInvariant();
            int phaseCode = phase == "ALL" ? 0 : phase == "A" ? 1 : phase == "B" ? 2 : 3;
            if (parameters[row, 0] != phaseCode) return "source_phase_mismatch";
            double parameterVoltage = parameters[row, 2];
            if (!Finite(parameterVoltage) || Math.Abs(parameterVoltage - voltage) > 1e-9 * Math.Max(1, Math.Abs(voltage)))
                return "source_voltage_mismatch";
            string stamp = Regex.Replace(match.Groups["stamp"].Value, @"[^0-9]", "");
            DateTime local;
            if (!DateTime.TryParseExact(stamp, "yyyyMMddHHmmss", Inv, DateTimeStyles.None, out local)) return "invalid_filename_date";
            double utc = double.NaN;
            if (!double.IsNaN(offset) && !TryUtc(local, offset, out utc)) return "utc_date_out_of_range";
            output[row, 3] = double.Parse(stamp, Inv);
            output[row, 4] = utc; output[row, 5] = offset;
            return double.IsNaN(offset) ? "local_time_only" : "ok";
        }

        private static bool TryUtc(DateTime local, double offset, out double seconds)
        {
            seconds = double.NaN;
            try
            {
                var instant = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.FromMinutes(offset));
                seconds = (instant.UtcDateTime - Epoch).TotalSeconds;
                return true;
            }
            catch (ArgumentException) { return false; }
        }

        private static void ValidateHistoricalTime(double[,] table, int row)
        {
            double encoded = table[row, 3], utc = table[row, 4], offset = table[row, 5];
            if (double.IsNaN(encoded))
            {
                if (!double.IsNaN(utc) || !double.IsNaN(offset))
                    throw new ArgumentException("HistoryTimeInconsistent: missing local time requires all three time fields to be NaN.");
                return;
            }
            DateTime local;
            if (!Finite(encoded) || encoded < 0 || encoded > 99991231235959d || encoded != Math.Floor(encoded) ||
                !DateTime.TryParseExact(encoded.ToString("00000000000000", Inv), "yyyyMMddHHmmss", Inv, DateTimeStyles.None, out local))
                throw new ArgumentException("HistoryTimeInvalid: invalid yyyyMMddHHmmss in time row " + row.ToString(Inv) + ".");
            ValidateOffset(offset);
            double expected;
            if (double.IsNaN(offset) ? !double.IsNaN(utc) : !TryUtc(local, offset, out expected) || !Finite(utc) || expected != utc)
                throw new ArgumentException("HistoryTimeInconsistent: local time, UTC time and offset disagree in row " + row.ToString(Inv) + ".");
        }

        private static string Success(double[,] table, int historicalRows, List<string> diagnostics)
        {
            int localKnown = 0, utcKnown = 0;
            for (int r = 0; r < table.GetLength(0); r++)
            { if (Finite(table[r, 3])) localKnown++; if (Finite(table[r, 4])) utcKnown++; }
            return "{\"ok\":true,\"schema\":\"task17.measurement_time.v1\",\"columns\":[\"source_row\",\"pdmap_id\",\"location_data_id\",\"local_yyyyMMddHHmmss\",\"utc_unix_seconds\",\"utc_offset_minutes\"]," +
                "\"source\":\"explicit_row_filename\",\"file_io\":false,\"machine_timezone_used\":false,\"parameter_matrix_modified\":false," +
                "\"historical_time_values_preserved\":true,\"time_precision\":\"one_second\",\"local_code_is_not_duration\":true," +
                "\"rows\":" + table.GetLength(0).ToString(Inv) + ",\"historical_rows\":" + historicalRows.ToString(Inv) +
                ",\"local_time_known_rows\":" + localKnown.ToString(Inv) + ",\"utc_time_known_rows\":" + utcKnown.ToString(Inv) +
                ",\"complete_local_times\":" + (localKnown == table.GetLength(0) ? "true" : "false") +
                ",\"complete_utc_times\":" + (utcKnown == table.GetLength(0) ? "true" : "false") +
                ",\"row_diagnostics\":[" + string.Join(",", diagnostics) + "]}";
        }

        private static string RowDiagnostic(int row, string code)
        { return "{\"row\":" + row.ToString(Inv) + ",\"code\":" + Json(code) + "}"; }
        private static string Failure(string message)
        { return "{\"ok\":false,\"schema\":\"task17.measurement_time.v1\",\"output_empty\":true,\"message\":" + Json(message) + "}"; }
        private static string Json(string value)
        {
            var text = new StringBuilder("\"");
            foreach (char ch in value ?? "")
            {
                if (ch == '"') text.Append("\\\"");
                else if (ch == '\\') text.Append("\\\\");
                else if (ch < 32) text.Append("\\u").Append(((int)ch).ToString("x4", Inv));
                else text.Append(ch);
            }
            return text.Append('"').ToString();
        }
    }
}
