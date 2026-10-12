using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace LabVIEWLocationTree
{
    public sealed class LocationRecord
    {
        public int Phase;
        public double DataId;
        public double VoltageKv;
        internal bool ZeroVoltageAnalysisOptIn;
        public string NoiseReferenceState="Pending";
        public int Sequence;
        public string DataPath;
        public bool Analyzed;
        public bool Selected;
        public List<MapRecord> Maps = new List<MapRecord>();
    }

    public sealed class MapRecord
    {
        public int Id;
        public int RowIndex;
        public bool Accepted;
        public bool Reviewed;
        public bool Removed;
        public bool AcceptanceConfirmed;
        // Display the same acceptance decision used by result statistics.
        // Manual confirmation remains separate provenance, not a second gate.
        public bool IncludedInResults { get { return Accepted&&!Removed; } }
        public bool ManuallyAccepted { get { return IncludedInResults&&AcceptanceConfirmed; } }
    }

    /// <summary>
    /// UI-independent tree state. Paths, (phase, data ID), and matrix row indices
    /// are separate identities. No analysis state is inferred from file voltage.
    /// All matrix access returns a copy; use SetMapAccepted to edit acceptance.
    /// </summary>
    public sealed class TreeDataModel
    {
        private const double MaxExactInteger = 9007199254740991d;
        private readonly List<LocationRecord> records = new List<LocationRecord>();
        private Dictionary<string, LocationRecord> recordsByPath =
            new Dictionary<string, LocationRecord>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<int, MapRecord> mapsById = new Dictionary<int, MapRecord>();
        private Dictionary<int, MapRecord> mapsByRow = new Dictionary<int, MapRecord>();
        private double[,] parameterMatrix = new double[0, 36];
        private string projectPath = String.Empty;
        private string[] warnings = new string[0];

        private static readonly Regex LocationName = new Regex(
            @"^(?:(?<phase>ALL|L1|L2|L3|A|B|C)_|_)(?<voltage>[+]?(?:\d+(?:\.\d*)?|\.\d+))\s*kV_(?<tail>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex IntegerSuffix = new Regex(@"^\d+$", RegexOptions.CultureInvariant);
        private static readonly Regex TimestampSuffix = new Regex(
            @"^(?<year>\d{4})-(?<month>\d{2})-(?<day>\d{2})-(?<hour>\d{2})'(?<minute>\d{2})'(?<second>\d{2})(?:\.(?<fraction>\d{1,7}))?'?$",
            RegexOptions.CultureInvariant);

        public List<LocationRecord> Records { get { return records; } }
        public bool AnalyzeZeroVoltageData { get; set; }
        internal bool CanAnalyze(LocationRecord record)
        {return record!=null&&(record.VoltageKv>0||AnalyzeZeroVoltageData&&record.VoltageKv==0&&record.ZeroVoltageAnalysisOptIn);}
        public string ProjectPath { get { return projectPath; } }
        public string[] Warnings { get { return (string[])warnings.Clone(); } }

        /// <summary>
        /// Scans this project only. Generated IDs start at zero in sorted tree
        /// order; these are NOT historical analysis IDs. Supply SetDataIds when
        /// the host's result matrix uses a different location ID assignment.
        /// </summary>
        public void LoadProject(string projectPath)
        {
            string root = NormalizePath(projectPath, false);
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException("Project directory does not exist: " + root);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Project directory must not be a symbolic link or junction.", "projectPath");

            List<string> scanWarnings = new List<string>();
            bool capacitor=HVX.ProjectModel.ProjectMeasurementMode.IsCapacitor(root);
            List<ScanCandidate> candidates = new List<ScanCandidate>();
            Stack<string> directories = new Stack<string>();
            directories.Push(root);
            while (directories.Count != 0)
            {
                string directory = directories.Pop();
                if (!String.Equals(directory, root, StringComparison.OrdinalIgnoreCase) &&
                    (File.Exists(Path.Combine(directory, "ITEM.in")) || File.Exists(Path.Combine(directory, "CABLE.in"))))
                {
                    scanWarnings.Add("Skipped nested project boundary: " + directory);
                    continue;
                }

                string[] files;
                string[] children;
                try
                {
                    files = Directory.GetFiles(directory);
                    children = Directory.GetDirectories(directory);
                }
                catch (UnauthorizedAccessException ex)
                {
                    scanWarnings.Add("Cannot read directory " + directory + ": " + ex.Message);
                    continue;
                }
                catch (IOException ex)
                {
                    scanWarnings.Add("Cannot read directory " + directory + ": " + ex.Message);
                    continue;
                }

                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string file in files)
                {
                    bool dem=capacitor&&String.Equals(directory,root,StringComparison.OrdinalIgnoreCase)&&HVX.PdReview.ReviewCapacitorDemInput.IsDem(file);
                    if (!String.Equals(Path.GetExtension(file), ".loc", StringComparison.OrdinalIgnoreCase)&&!dem)
                        continue;
                    try
                    {
                        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        {
                            scanWarnings.Add("Skipped linked LOC file: " + file);
                            continue;
                        }
                        ScanCandidate candidate;
                        string reason;
                        if(dem)HVX.PdReview.ReviewCapacitorDemInput.Metadata(file);
                        if (TryParseLocation(file, root, out candidate, out reason))
                            candidates.Add(candidate);
                        else
                            scanWarnings.Add("Skipped LOC file " + file + ": " + reason);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        scanWarnings.Add("Cannot inspect LOC file " + file + ": " + ex.Message);
                    }
                    catch (IOException ex)
                    {
                        scanWarnings.Add("Cannot inspect LOC file " + file + ": " + ex.Message);
                    }
                }

                Array.Sort(children, StringComparer.OrdinalIgnoreCase);
                for (int i = children.Length - 1; i >= 0; i--)
                {
                    try
                    {
                        if ((File.GetAttributes(children[i]) & FileAttributes.ReparsePoint) != 0)
                            scanWarnings.Add("Skipped linked directory: " + children[i]);
                        else
                            directories.Push(children[i]);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        scanWarnings.Add("Cannot inspect directory " + children[i] + ": " + ex.Message);
                    }
                    catch (IOException ex)
                    {
                        scanWarnings.Add("Cannot inspect directory " + children[i] + ": " + ex.Message);
                    }
                }
            }

            // Numeric sequence suffixes are retained. Timestamp files receive
            // chronological 1-based sequence numbers within phase + voltage.
            candidates.Sort(CompareCandidates);
            int timestampSequence = 0;
            int previousPhase = -1;
            double previousVoltage = -1;
            foreach (ScanCandidate candidate in candidates)
            {
                if (candidate.Record.Phase != previousPhase || candidate.Record.VoltageKv != previousVoltage)
                {
                    timestampSequence = 0;
                    previousPhase = candidate.Record.Phase;
                    previousVoltage = candidate.Record.VoltageKv;
                }
                if (candidate.HasTimestamp)
                    candidate.Record.Sequence = ++timestampSequence;
            }
            List<LocationRecord> loaded = new List<LocationRecord>();
            foreach (ScanCandidate candidate in candidates)
                loaded.Add(candidate.Record);
            loaded.Sort(CompareRecords);
            for (int i = 0; i < loaded.Count; i++)
                loaded[i].DataId = i;
            CommitRecords(loaded, root, scanWarnings.ToArray());
        }

        /// <summary>Loads absolute paths supplied by the host; files need not exist.</summary>
        public void LoadRecords(string[] paths, int[] phases, double[] voltages, int[] sequences, double[] ids)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            if (phases == null) throw new ArgumentNullException("phases");
            if (voltages == null) throw new ArgumentNullException("voltages");
            if (sequences == null) throw new ArgumentNullException("sequences");
            if (ids == null) throw new ArgumentNullException("ids");
            int count = paths.Length;
            if (phases.Length != count || voltages.Length != count || sequences.Length != count || ids.Length != count)
                throw new ArgumentException("Paths, phases, voltages, sequences, and IDs must have equal lengths.");

            List<LocationRecord> loaded = new List<LocationRecord>();
            HashSet<string> seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<RecordKey> seenIds = new HashSet<RecordKey>();
            for (int i = 0; i < count; i++)
            {
                string path = NormalizePath(paths[i], true);
                ValidatePhase(phases[i], "phases");
                ValidateVoltage(voltages[i], "voltages");
                ValidateDataId(ids[i], "ids");
                if (sequences[i] < 0)
                    throw new ArgumentException("Sequence must be a nonnegative integer at input index " + i + ".", "sequences");
                if (!seenPaths.Add(path))
                    throw new ArgumentException("Duplicate location path: " + path, "paths");
                if (!seenIds.Add(new RecordKey(phases[i], ids[i])))
                    throw new ArgumentException("Duplicate location ID within phase at input index " + i + ".", "ids");
                loaded.Add(new LocationRecord
                {
                    DataPath = path,
                    Phase = phases[i],
                    VoltageKv = voltages[i],
                    Sequence = sequences[i],
                    DataId = ids[i]
                });
            }
            loaded.Sort(CompareRecords);
            CommitRecords(loaded, String.Empty, new string[0]);
        }

        /// <summary>Atomically maps paths to algorithm IDs before any analysis results are loaded.</summary>
        public void SetDataIds(string[] paths, double[] ids)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            if (ids == null) throw new ArgumentNullException("ids");
            if (paths.Length != ids.Length)
                throw new ArgumentException("Paths and IDs must have equal lengths.");
            if (parameterMatrix.GetLength(0) != 0)
                throw new InvalidOperationException("Location IDs cannot be changed after result rows have been loaded.");
            foreach (LocationRecord record in records)
                if (record.Analyzed)
                    throw new InvalidOperationException("Location IDs cannot be changed after any location has been marked analyzed.");

            Dictionary<LocationRecord, double> replacements = new Dictionary<LocationRecord, double>();
            for (int i = 0; i < paths.Length; i++)
            {
                ValidateDataId(ids[i], "ids");
                LocationRecord record = RequireRecord(paths[i]);
                if (replacements.ContainsKey(record))
                    throw new ArgumentException("Duplicate location path: " + record.DataPath, "paths");
                replacements.Add(record, ids[i]);
            }
            HashSet<RecordKey> seen = new HashSet<RecordKey>();
            foreach (LocationRecord record in records)
            {
                double id;
                if (!replacements.TryGetValue(record, out id)) id = record.DataId;
                if (!seen.Add(new RecordKey(record.Phase, id)))
                    throw new ArgumentException("Resulting IDs contain a duplicate within phase " + record.Phase + ".", "ids");
            }
            foreach (KeyValuePair<LocationRecord, double> pair in replacements)
                pair.Key.DataId = pair.Value;
        }

        /// <summary>
        /// Replaces all map rows. Columns: phase=0, location ID=1, kV=2,
        /// per-location map ID=9, accepted=35. RowIndex is zero-based and
        /// preserves input row order, regardless of displayed map sorting.
        /// </summary>
        internal bool LastMatrixStructureChanged {get;private set;}
        internal bool LastMatrixValueChanged {get;private set;}
        internal int[] LastChangedStateRows {get;private set;} = new int[0];
        public void SetParameterMatrix(double[,] matrix)
        {
            if (matrix == null) throw new ArgumentNullException("matrix");
            int rowCount = matrix.GetLength(0);
            if (rowCount != 0 && matrix.GetLength(1) < 36)
                throw new ArgumentException("A nonempty parameter matrix requires at least 36 columns.", "matrix");
            bool same=parameterMatrix.GetLength(0)==rowCount&&parameterMatrix.GetLength(1)==matrix.GetLength(1)&&mapsByRow.Count==rowCount;
            for(int row=0;same&&row<rowCount;row++)
                same=matrix[row,0]==parameterMatrix[row,0]&&matrix[row,1]==parameterMatrix[row,1]&&matrix[row,2]==parameterMatrix[row,2]&&matrix[row,9]==parameterMatrix[row,9];
            if(same){
                for(int row=0;row<rowCount;row++)if(matrix[row,35]!=0&&matrix[row,35]!=1)throw new ArgumentException("Acceptance must be exactly 0 or 1.","matrix");
                bool changed=false;var stateRows=new List<int>();
                for(int row=0;row<rowCount;row++){
                    if(mapsByRow[row].Accepted!=(matrix[row,35]==1))stateRows.Add(row);
                    for(int column=0;column<matrix.GetLength(1);column++)if(!matrix[row,column].Equals(parameterMatrix[row,column]))changed=true;
                }
                LastMatrixStructureChanged=false;LastMatrixValueChanged=changed;LastChangedStateRows=stateRows.ToArray();
                if(!changed)return;
                parameterMatrix=(double[,])matrix.Clone();
                foreach(int row in LastChangedStateRows)mapsByRow[row].Accepted=parameterMatrix[row,35]==1;
                return;
            }
            double[,] nextMatrix = (double[,])matrix.Clone();
            Dictionary<RecordKey, LocationRecord> byIdentity = new Dictionary<RecordKey, LocationRecord>();
            Dictionary<LocationRecord, List<MapRecord>> nextLists = new Dictionary<LocationRecord, List<MapRecord>>();
            Dictionary<int, MapRecord> nextMaps = new Dictionary<int, MapRecord>();
            Dictionary<int, MapRecord> nextRows = new Dictionary<int, MapRecord>();
            foreach (LocationRecord record in records)
            {
                byIdentity.Add(new RecordKey(record.Phase, record.DataId), record);
                nextLists.Add(record, new List<MapRecord>());
            }
            for (int row = 0; row < rowCount; row++)
            {
                string prefix = "Matrix row " + row + ": ";
                double phaseValue = nextMatrix[row, 0];
                if (!IsFinite(phaseValue) || phaseValue != Math.Floor(phaseValue) || phaseValue < 0 || phaseValue > 3)
                    throw new ArgumentException(prefix + "phase column 0 must be 0, 1, 2, or 3.", "matrix");
                double dataId = nextMatrix[row, 1];
                if (!IsValidDataId(dataId))
                    throw new ArgumentException(prefix + "location ID column 1 must be an exact nonnegative integer <= 2^53 - 1.", "matrix");
                double voltage = nextMatrix[row, 2];
                if (!IsFinite(voltage) || voltage < 0)
                    throw new ArgumentException(prefix + "voltage column 2 must be finite and nonnegative.", "matrix");
                double mapValue = nextMatrix[row, 9];
                if (!IsFinite(mapValue) || mapValue != Math.Floor(mapValue) || mapValue < 0 || mapValue > Int32.MaxValue)
                    throw new ArgumentException(prefix + "map ID column 9 must be a nonnegative Int32.", "matrix");
                double accepted = nextMatrix[row, 35];
                if (accepted != 0d && accepted != 1d)
                    throw new ArgumentException(prefix + "acceptance column 35 must be exactly 0 or 1.", "matrix");
                LocationRecord parent;
                if (!byIdentity.TryGetValue(new RecordKey((int)phaseValue, dataId), out parent))
                    throw new ArgumentException(prefix + "unknown location identity (phase " + phaseValue.ToString(CultureInfo.InvariantCulture) + ", ID " + dataId.ToString("R", CultureInfo.InvariantCulture) + ").", "matrix");
                if (Math.Abs(parent.VoltageKv - voltage) > 0.011d + 1e-12d)
                    throw new ArgumentException(prefix + "voltage does not match the location record within 0.011 kV.", "matrix");
                int mapId = (int)mapValue;
                if (nextLists[parent].Exists(delegate(MapRecord item) { return item.Id == mapId; }))
                    throw new ArgumentException(prefix + "duplicate map ID within one location " + mapId + ".", "matrix");
                MapRecord map = new MapRecord { Id = mapId, RowIndex = row, Accepted = accepted == 1d };
                // A local ID can recur in another LOC file. A null ID lookup
                // is deliberately ambiguous; source-row selection stays exact.
                if (nextMaps.ContainsKey(mapId)) nextMaps[mapId] = null;
                else nextMaps.Add(mapId, map);
                nextRows.Add(row, map);
                nextLists[parent].Add(map);
            }
            foreach (List<MapRecord> list in nextLists.Values)
                list.Sort(delegate(MapRecord left, MapRecord right) { return left.Id.CompareTo(right.Id); });

            // Commit only after every row has passed validation.
            parameterMatrix = nextMatrix;
            LastMatrixStructureChanged=LastMatrixValueChanged=true;
            LastChangedStateRows=new int[0];
            mapsById = nextMaps;
            mapsByRow = nextRows;
            foreach (LocationRecord record in records)
            {
                record.Maps = nextLists[record];
                if (record.Maps.Count != 0)
                {
                    record.Analyzed = true;
                    record.Selected = false;
                }
                // Previously completed records stay completed even if no map
                // remains. No-map pending records remain pending.
            }
        }

        public double[,] GetParameterMatrix()
        {
            return (double[,])parameterMatrix.Clone();
        }

        public void SetReviewedRows(bool[] values)
        {
            ValidateReviewState(values);UpdateReviewStates(values,null,null);
        }

        public bool[] GetReviewedRows()
        {
            var values = new bool[parameterMatrix.GetLength(0)];
            foreach (var item in mapsByRow) values[item.Key] = item.Value.Reviewed;
            return values;
        }

        public void SetRemovedRows(bool[] values)
        {
            ValidateReviewState(values);UpdateReviewStates(null,values,null);
        }

        public bool[] GetRemovedRows()
        {
            var values = new bool[parameterMatrix.GetLength(0)];
            foreach (var item in mapsByRow) values[item.Key] = item.Value.Removed;
            return values;
        }

        public void SetAcceptanceConfirmedRows(bool[] values)
        {
            ValidateReviewState(values);UpdateReviewStates(null,null,values);
        }
        // Validate all three snapshots before any mutation. A row is always the
        // current matrix row, not a MAP ID shared across different LOC files.
        public void SetReviewStates(bool[] reviewed,bool[] removed,bool[] confirmed)
        {
            ValidateReviewState(reviewed);ValidateReviewState(removed);ValidateReviewState(confirmed);
            UpdateReviewStates(reviewed,removed,confirmed);
        }
        internal void SetParameterMatrixAndReviewStates(double[,] matrix,bool[] reviewed,bool[] removed,bool[] confirmed)
        {
            if(matrix==null)throw new ArgumentNullException("matrix");
            int count=matrix.GetLength(0);
            if(reviewed==null||removed==null||confirmed==null||reviewed.Length!=count||removed.Length!=count||confirmed.Length!=count)
                throw new ArgumentException("Review snapshots must match the new matrix row count.");
            SetParameterMatrix(matrix);int[] acceptedRows=LastChangedStateRows;
            UpdateReviewStates(reviewed,removed,confirmed);
            var changed=new HashSet<int>(acceptedRows);foreach(int row in LastChangedStateRows)changed.Add(row);
            LastChangedStateRows=new List<int>(changed).ToArray();
        }
        void ValidateReviewState(bool[] values)
        {
            if(values==null||values.Length!=parameterMatrix.GetLength(0))throw new ArgumentException("Review state must match the current matrix row count.","values");
        }
        void UpdateReviewStates(bool[] reviewed,bool[] removed,bool[] confirmed)
        {
            var changed=new List<int>();
            foreach(var item in mapsByRow){
                int row=item.Key;MapRecord map=item.Value;
                if((reviewed!=null&&map.Reviewed!=reviewed[row])||(removed!=null&&map.Removed!=removed[row])||(confirmed!=null&&map.AcceptanceConfirmed!=confirmed[row]))changed.Add(row);
                if(reviewed!=null)map.Reviewed=reviewed[row];if(removed!=null)map.Removed=removed[row];if(confirmed!=null)map.AcceptanceConfirmed=confirmed[row];
            }
            LastChangedStateRows=changed.ToArray();
        }

        /// <summary>Marks successful analysis completion, including a valid no-discharge result.</summary>
        public void MarkAnalyzed(string[] paths)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            List<LocationRecord> completed = new List<LocationRecord>();
            foreach (string path in paths)
                completed.Add(RequireRecord(path));
            foreach (LocationRecord record in completed)
            {
                record.Analyzed = true;
                record.Selected = false;
            }
        }

        /// <returns>True only when a pending record changes selection.</returns>
        public bool SetRecordSelected(string path, bool value)
        {
            LocationRecord record = FindRecord(path);
            if (!CanAnalyze(record) || record.Analyzed || record.Selected == value) return false;
            record.Selected = value;
            return true;
        }

        /// <returns>True only when at least one pending record changes selection.</returns>
        public bool SetPhaseSelected(int phase, bool value)
        {
            ValidatePhase(phase, "phase");
            bool changed = false;
            foreach (LocationRecord record in records)
            {
                if (record.Phase == phase && CanAnalyze(record) && !record.Analyzed && record.Selected != value)
                {
                    record.Selected = value;
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>-1 = no selectable records, 0 = none checked, 1 = all checked, 2 = mixed.</summary>
        public int GetPhaseCheckState(int phase)
        {
            ValidatePhase(phase, "phase");
            int pending = 0;
            int selected = 0;
            foreach (LocationRecord record in records)
            {
                if (record.Phase != phase || !CanAnalyze(record) || record.Analyzed) continue;
                pending++;
                if (record.Selected) selected++;
            }
            if (pending == 0) return -1;
            if (selected == 0) return 0;
            return selected == pending ? 1 : 2;
        }

        public bool SetMapAccepted(int mapId, bool value)
        {
            MapRecord map = FindMap(mapId);
            return SetAccepted(map, value);
        }

        public bool SetMapAcceptedBySourceRow(int sourceRow, bool value)
        {
            return SetAccepted(FindMapBySourceRow(sourceRow), value);
        }

        private bool SetAccepted(MapRecord map, bool value)
        {
            if (map == null || map.Accepted == value) return false;
            parameterMatrix[map.RowIndex, 35] = value ? 1d : 0d;
            map.Accepted = value;
            return true;
        }

        /// <summary>Clears analysis results for these LOC identities, without touching disk.</summary>
        public void ResetAnalysisForPaths(string[] paths)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            HashSet<LocationRecord> reset = new HashSet<LocationRecord>();
            foreach (string path in paths) reset.Add(RequireRecord(path));
            if (reset.Count == 0) return;
            HashSet<int> removedRows = new HashSet<int>();
            foreach (LocationRecord record in reset)
                foreach (MapRecord map in record.Maps) removedRows.Add(map.RowIndex);
            double[,] remaining = new double[parameterMatrix.GetLength(0) - removedRows.Count, parameterMatrix.GetLength(1)];
            int targetRow = 0;
            for (int row = 0; row < parameterMatrix.GetLength(0); row++)
            {
                if (removedRows.Contains(row)) continue;
                for (int column = 0; column < parameterMatrix.GetLength(1); column++)
                    remaining[targetRow, column] = parameterMatrix[row, column];
                targetRow++;
            }
            SetParameterMatrix(remaining);
            foreach (LocationRecord record in reset)
            {
                record.Analyzed = false;
                record.Selected = false;
            }
        }

        public string[] GetSelectedDataPaths()
        {
            List<string> paths = new List<string>();
            foreach (LocationRecord record in records)
                if (CanAnalyze(record) && !record.Analyzed && record.Selected) paths.Add(record.DataPath);
            return paths.ToArray();
        }

        public LocationRecord FindRecord(string path)
        {
            LocationRecord record;
            recordsByPath.TryGetValue(NormalizePath(path, true), out record);
            return record;
        }

        public MapRecord FindMap(int id)
        {
            MapRecord map;
            if (mapsById.TryGetValue(id, out map) && map == null)
                throw new ArgumentException("Map ID occurs in multiple LOC files; select by source row.", "id");
            return map;
        }

        public MapRecord FindMapBySourceRow(int sourceRow)
        {
            MapRecord map;
            mapsByRow.TryGetValue(sourceRow, out map);
            return map;
        }

        private LocationRecord RequireRecord(string path)
        {
            LocationRecord record = FindRecord(path);
            if (record == null)
                throw new ArgumentException("Unknown location path: " + path, "paths");
            return record;
        }

        private void CommitRecords(List<LocationRecord> loaded, string root, string[] nextWarnings)
        {
            Dictionary<string, LocationRecord> nextPaths = new Dictionary<string, LocationRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (LocationRecord record in loaded){
                record.ZeroVoltageAnalysisOptIn=record.VoltageKv==0&&(HVX.PdReview.ReviewCapacitorDemInput.IsDem(record.DataPath)?
                    HVX.PdReview.ReviewCapacitorDemInput.ZeroOptIn(record.DataPath):HVX.PdReview.ReviewLocationFileReader.IsZeroVoltageAnalysisRecord(record.DataPath));
                nextPaths.Add(record.DataPath, record);
            }
            records.Clear();
            records.AddRange(loaded);
            recordsByPath = nextPaths;
            mapsById = new Dictionary<int, MapRecord>();
            mapsByRow = new Dictionary<int, MapRecord>();
            parameterMatrix = new double[0, 36];
            projectPath = root;
            warnings = nextWarnings;
        }

        private static bool TryParseLocation(string file, string root, out ScanCandidate candidate, out string reason)
        {
            candidate = null;
            reason = null;
            Match match = LocationName.Match(Path.GetFileNameWithoutExtension(file));
            if (!match.Success)
            {
                reason = "expected A_18.30kV_1.loc, a timestamp suffix, or legacy _18.30kV_...loc in a phase directory";
                return false;
            }
            int phase;
            if (!TryPhase(match.Groups["phase"].Value, out phase) && !TryDirectoryPhase(Path.GetDirectoryName(file), root, out phase))
            {
                reason = "phase is missing from both the filename and its parent directories";
                return false;
            }
            double voltage;
            if (!Double.TryParse(match.Groups["voltage"].Value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out voltage) || !IsFinite(voltage) || voltage < 0)
            {
                reason = "voltage is not a finite nonnegative number";
                return false;
            }
            string suffix = match.Groups["tail"].Value;
            int sequence = 0;
            bool hasTimestamp = false;
            DateTime timestamp = DateTime.MinValue;
            if (IntegerSuffix.IsMatch(suffix))
            {
                if (!Int32.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out sequence))
                {
                    reason = "numeric sequence exceeds Int32 range";
                    return false;
                }
            }
            else if (TryTimestamp(suffix, out timestamp))
                hasTimestamp = true;
            else
            {
                reason = "suffix must be a nonnegative numeric sequence or yyyy-MM-dd-HH'mm'ss timestamp";
                return false;
            }
            candidate = new ScanCandidate
            {
                Record = new LocationRecord { Phase = phase, VoltageKv = voltage, Sequence = sequence, DataPath = Path.GetFullPath(file) },
                HasTimestamp = hasTimestamp,
                Timestamp = timestamp
            };
            return true;
        }

        private static bool TryTimestamp(string suffix, out DateTime timestamp)
        {
            timestamp = DateTime.MinValue;
            Match match = TimestampSuffix.Match(suffix);
            if (!match.Success) return false;
            try
            {
                timestamp = new DateTime(
                    Int32.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture),
                    Int32.Parse(match.Groups["month"].Value, CultureInfo.InvariantCulture),
                    Int32.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture),
                    Int32.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture),
                    Int32.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture),
                    Int32.Parse(match.Groups["second"].Value, CultureInfo.InvariantCulture));
                string fraction = match.Groups["fraction"].Value;
                if (fraction.Length != 0)
                    timestamp = timestamp.AddTicks(Int32.Parse(fraction.PadRight(7, '0'), CultureInfo.InvariantCulture));
                return true;
            }
            catch (ArgumentOutOfRangeException) { return false; }
        }

        private static bool TryDirectoryPhase(string directory, string root, out int phase)
        {
            while (!String.IsNullOrEmpty(directory))
            {
                string name = new DirectoryInfo(directory).Name;
                if (TryPhase(name, out phase)) return true;
                if (String.Equals(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                    break;
                DirectoryInfo parent = Directory.GetParent(directory);
                directory = parent == null ? null : parent.FullName;
            }
            phase = 0;
            return false;
        }

        private static bool TryPhase(string value, out int phase)
        {
            string normalized = value.Trim().ToUpperInvariant();
            if (normalized.EndsWith("相", StringComparison.Ordinal)) normalized = normalized.Substring(0, normalized.Length - 1);
            switch (normalized)
            {
                case "A": case "L1": phase = 1; return true;
                case "B": case "L2": phase = 2; return true;
                case "C": case "L3": phase = 3; return true;
                case "ALL": phase = 0; return true;
                default: phase = 0; return false;
            }
        }

        private static string NormalizePath(string path, bool requireAbsolute)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path must not be empty.", "path");
            string root = Path.GetPathRoot(path);
            if (requireAbsolute && (String.IsNullOrEmpty(root) ||
                (root.Length == 1 && (root[0] == Path.DirectorySeparatorChar || root[0] == Path.AltDirectorySeparatorChar)) ||
                (root.Length == 2 && root[1] == ':')))
                throw new ArgumentException("An absolute location path is required: " + path, "path");
            return Path.GetFullPath(path);
        }

        private static int CompareRecords(LocationRecord left, LocationRecord right)
        {
            int result = PhaseOrder(left.Phase).CompareTo(PhaseOrder(right.Phase));
            if (result != 0) return result;
            result = left.VoltageKv.CompareTo(right.VoltageKv);
            if (result != 0) return result;
            result = left.Sequence.CompareTo(right.Sequence);
            if (result != 0) return result;
            result = StringComparer.OrdinalIgnoreCase.Compare(left.DataPath, right.DataPath);
            return result != 0 ? result : StringComparer.Ordinal.Compare(left.DataPath, right.DataPath);
        }

        private static int CompareCandidates(ScanCandidate left, ScanCandidate right)
        {
            int result = PhaseOrder(left.Record.Phase).CompareTo(PhaseOrder(right.Record.Phase));
            if (result != 0) return result;
            result = left.Record.VoltageKv.CompareTo(right.Record.VoltageKv);
            if (result != 0) return result;
            result = left.Timestamp.CompareTo(right.Timestamp);
            if (result != 0) return result;
            return StringComparer.OrdinalIgnoreCase.Compare(left.Record.DataPath, right.Record.DataPath);
        }

        private static int PhaseOrder(int phase) { return phase == 0 ? 4 : phase; }
        private static bool IsFinite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static bool IsValidDataId(double value)
        {
            return IsFinite(value) && value >= 0d && value <= MaxExactInteger && value == Math.Floor(value);
        }
        private static void ValidateDataId(double value, string name)
        {
            if (!IsValidDataId(value))
                throw new ArgumentException("Location IDs must be exact nonnegative integers <= 2^53 - 1.", name);
        }
        private static void ValidateVoltage(double value, string name)
        {
            if (!IsFinite(value) || value < 0d)
                throw new ArgumentException("Voltages must be finite and nonnegative.", name);
        }
        private static void ValidatePhase(int phase, string name)
        {
            if (phase < 0 || phase > 3)
                throw new ArgumentException("Phase must be 1 (A), 2 (B), 3 (C), or 0 (ALL).", name);
        }

        private sealed class ScanCandidate
        {
            public LocationRecord Record;
            public bool HasTimestamp;
            public DateTime Timestamp;
        }

        private struct RecordKey : IEquatable<RecordKey>
        {
            private readonly int phase;
            private readonly double id;
            public RecordKey(int phase, double id) { this.phase = phase; this.id = id; }
            public bool Equals(RecordKey other) { return phase == other.phase && id == other.id; }
            public override bool Equals(object obj) { return obj is RecordKey && Equals((RecordKey)obj); }
            public override int GetHashCode() { unchecked { return (phase * 397) ^ id.GetHashCode(); } }
        }
    }
}
