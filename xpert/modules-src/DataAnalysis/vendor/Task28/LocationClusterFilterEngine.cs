// Task28 source version 1.2.1.0; compatible with the 100 host matrix/cache contract.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;


namespace HVX.PD.LocationFiltering
{
    /// <summary>
    /// Filters already accepted location rows. Scores are engineering evidence scores,
    /// not defect probabilities or a reproduction of a vendor's ClusterStrength formula.
    /// All mutable state is confined to one method invocation.
    /// </summary>
    internal sealed class LocationClusterFilterEngine
    {
        private const int RequiredColumns = 36;
        private const int MaximumRows = 200000;
        private const long MaximumMatrixCells = 10000000;
        private const int EndpointFlag = 1;
        private const int MissingSigmaFlag = 2;
        private const int MissingEvidenceFlag = 4;
        private const int MissingArrivalFlag = 8;
        private const int CompensationFallbackFlag = 16;
        private const int RadiusCappedFlag = 32;

        public LocationClusterFilterEngine() { }

        // Shared with host adapters so a scoring-copy allocation cannot bypass
        // the same bounded matrix contract enforced by the engine.
        internal static void ValidateInputMatrix(double[,] parameterMatrix)
        {
            if (parameterMatrix == null)
                throw new ArgumentException("parameterMatrix must not be null.", "parameterMatrix");
            if (parameterMatrix.GetLowerBound(0) != 0 || parameterMatrix.GetLowerBound(1) != 0)
                throw new ArgumentException("parameterMatrix must use zero-based row and column indices.", "parameterMatrix");
            ValidateInputDimensions(parameterMatrix.GetLength(0), parameterMatrix.GetLength(1));
        }

        internal static void ValidateInputDimensions(int rowCount, int columnCount)
        {
            if (rowCount < 0 || rowCount > MaximumRows)
                throw new ArgumentException("parameterMatrix supports at most 200000 rows per call.", "parameterMatrix");
            if (columnCount < RequiredColumns)
                throw new ArgumentException("parameterMatrix requires at least 36 columns in the Task17 mapping.", "parameterMatrix");
            if ((long)rowCount * columnCount > MaximumMatrixCells)
                throw new ArgumentException("parameterMatrix supports at most 10000000 total cells per call, including extension columns.", "parameterMatrix");
        }

        public void FilterLocationMatrix(double[,] parameterMatrix, double clusterStrength,
            out double[,] filteredParameterMatrix, out double[,] gatedParameterMatrix,
            out int[] retainedRowIndices, out double[,] rowDiagnostics,
            out double[,] clusterSummary, out string statusJson)
        {
            FilterLocationMatrixAdvanced(parameterMatrix, clusterStrength, -1, -1, true, true,
                out filteredParameterMatrix, out gatedParameterMatrix, out retainedRowIndices,
                out rowDiagnostics, out clusterSummary, out statusJson);
        }

        public void FilterLocationMatrixAdvanced(double[,] parameterMatrix, double clusterStrength,
            double positionRadiusM, int minimumIndependentRecords, bool preserveCableEnds,
            bool useCompensatedCharge, out double[,] filteredParameterMatrix,
            out double[,] gatedParameterMatrix, out int[] retainedRowIndices,
            out double[,] rowDiagnostics, out double[,] clusterSummary, out string statusJson)
        {
            ValidateInputMatrix(parameterMatrix);
            int rowCount = parameterMatrix.GetLength(0);
            int columnCount = parameterMatrix.GetLength(1);
            double strength = clusterStrength == -1 ? 40 : clusterStrength;
            if (!Finite(strength) || strength < 0 || strength > 100)
                throw new ArgumentException("clusterStrength must be -1 (default 40) or a finite value in [0,100].", "clusterStrength");
            if (!Finite(positionRadiusM) || (positionRadiusM != -1 && positionRadiusM <= 0))
                throw new ArgumentException("positionRadiusM must be -1 (automatic) or finite and positive.", "positionRadiusM");
            int minimumRecords = minimumIndependentRecords == -1 ? 3 : minimumIndependentRecords;
            if (minimumRecords < 2 || minimumRecords > MaximumRows)
                throw new ArgumentException("minimumIndependentRecords must be -1 or an integer from 2 to 200000.", "minimumIndependentRecords");

            Row[] rows = new Row[rowCount];
            Phase[] phases = new Phase[4];
            for (int p = 0; p < phases.Length; p++) phases[p] = new Phase(p);
            int originalRejected = 0, invalidRows = 0, backgroundRows = 0;
            int missingSigma = 0, missingEvidence = 0, missingArrival = 0, compensationFallback = 0;
            for (int i = 0; i < rowCount; i++)
            {
                Row row = new Row();
                row.Source = i;
                row.Cluster = -1;
                rows[i] = row;
                if (parameterMatrix[i, 35] != 1)
                {
                    row.Reason = -1;
                    originalRejected++;
                    continue;
                }
                double voltage = parameterMatrix[i, 2];
                if (!Finite(voltage))
                {
                    row.Reason = -2;
                    invalidRows++;
                    continue;
                }
                if (voltage == 0)
                {
                    row.Reason = -3;
                    backgroundRows++;
                    continue;
                }
                double phase = parameterMatrix[i, 0];
                double loc = parameterMatrix[i, 1];
                double length = parameterMatrix[i, 5];
                double x = parameterMatrix[i, 13];
                double rawQ = parameterMatrix[i, 15];
                if (!Finite(phase) || phase < 0 || phase > 3 || phase != Math.Floor(phase) ||
                    !Finite(loc) || loc < 0 || loc > 9007199254740991.0 || loc != Math.Floor(loc) ||
                    !Finite(length) || length <= 0 || !Finite(x) || x < 0 || x > length ||
                    !Finite(rawQ) || rawQ <= 0)
                {
                    row.Reason = -2;
                    invalidRows++;
                    continue;
                }
                row.Valid = true;
                row.Phase = (int)phase;
                row.Loc = loc == 0 ? 0 : loc;
                row.Length = length;
                row.X = x == 0 ? 0 : x;
                row.RawQ = rawQ;
                row.DisplayQ = rawQ;
                if (useCompensatedCharge)
                {
                    double compensated = parameterMatrix[i, 16];
                    if (Finite(compensated) && compensated > 0) row.DisplayQ = compensated;
                    else { row.Flags |= CompensationFallbackFlag; compensationFallback++; }
                }
                double sigma = parameterMatrix[i, 28];
                if (Finite(sigma) && sigma > 0)
                {
                    row.Snr = rawQ / sigma;
                    if (!Finite(row.Snr)) row.Snr = Double.MaxValue;
                    row.SignalQuality = Clamp((row.Snr - 1) / 9, 0, 1);
                }
                else
                {
                    row.Snr = 0;
                    row.SignalQuality = 0.5;
                    row.Flags |= MissingSigmaFlag;
                    missingSigma++;
                }
                double evidence = parameterMatrix[i, 22];
                if (Finite(evidence) && evidence >= 0 && evidence <= 1) row.Evidence = evidence;
                else
                {
                    row.Evidence = 0.5;
                    row.Flags |= MissingEvidenceFlag;
                    missingEvidence++;
                }
                double arrival = parameterMatrix[i, 10];
                if (Finite(arrival) && arrival >= 0 && arrival == Math.Floor(arrival))
                    row.Arrival = arrival == 0 ? 0 : arrival;
                else
                {
                    row.Arrival = -1;
                    row.Flags |= MissingArrivalFlag;
                    missingArrival++;
                }
                double endpointTolerance = Math.Max(1e-9, 1e-12 * length);
                row.Endpoint = x <= endpointTolerance ? 1 : (length - x <= endpointTolerance ? 2 : 0);
                if (row.Endpoint != 0) row.Flags |= EndpointFlag;
                double velocity = parameterMatrix[i, 6];
                double sampling = parameterMatrix[i, 7];
                row.Resolution = Finite(velocity) && velocity > 0 && Finite(sampling) && sampling > 0
                    ? SamplingResolution(velocity, sampling) : 0;

                phases[row.Phase].Rows.Add(row);
            }

            List<Cluster> clusters = new List<Cluster>();
            int distinctEvents = 0;
            for (int p = 0; p < phases.Length; p++)
            {
                Phase phase = phases[p];
                if (phase.Rows.Count == 0) continue;
                PreparePhase(phase, positionRadiusM);
                BuildClusters(phase, clusters, minimumRecords);
                distinctEvents += phase.Events.Count;
            }

            gatedParameterMatrix = (double[,])parameterMatrix.Clone();
            rowDiagnostics = new double[rowCount, 8];
            List<int> retained = new List<int>();
            int protectedRows = 0, strengthRejected = 0;
            for (int i = 0; i < rowCount; i++)
            {
                Row row = rows[i];
                bool keep = false;
                if (row.Valid)
                {
                    bool protect = preserveCableEnds && row.Endpoint != 0;
                    keep = strength == 0 || protect || row.DisplayScore >= strength;
                    row.Reason = keep ? (protect ? 2 : 1) : 3;
                    if (protect) protectedRows++;
                    if (!keep) strengthRejected++;
                }
                if (keep) retained.Add(i);
                else gatedParameterMatrix[i, 35] = 0;
                rowDiagnostics[i, 0] = i;
                rowDiagnostics[i, 1] = keep ? 1 : 0;
                rowDiagnostics[i, 2] = row.Cluster;
                rowDiagnostics[i, 3] = row.ClusterScore;
                rowDiagnostics[i, 4] = row.MemberScore;
                rowDiagnostics[i, 5] = row.DisplayScore;
                rowDiagnostics[i, 6] = row.Reason;
                rowDiagnostics[i, 7] = row.Flags;
            }
            retainedRowIndices = retained.ToArray();
            filteredParameterMatrix = new double[retained.Count, columnCount];
            for (int target = 0; target < retained.Count; target++)
                for (int c = 0; c < columnCount; c++)
                    filteredParameterMatrix[target, c] = parameterMatrix[retained[target], c];
            clusterSummary = new double[clusters.Count, 14];
            for (int i = 0; i < clusters.Count; i++)
            {
                Cluster cluster = clusters[i];
                clusterSummary[i, 0] = cluster.Id;
                clusterSummary[i, 1] = cluster.Phase.Id;
                clusterSummary[i, 2] = cluster.Center;
                clusterSummary[i, 3] = cluster.Endpoint == 0 ? cluster.Phase.Radius : 0;
                clusterSummary[i, 4] = cluster.MemberRows;
                clusterSummary[i, 5] = cluster.RecordCount;
                clusterSummary[i, 6] = cluster.Phase.RecordCoverage.Count;
                clusterSummary[i, 7] = cluster.Score;
                clusterSummary[i, 8] = cluster.BackgroundSupport;
                clusterSummary[i, 9] = cluster.MedianQ;
                clusterSummary[i, 10] = cluster.MedianSnr;
                clusterSummary[i, 11] = cluster.MedianEvidence;
                clusterSummary[i, 12] = cluster.Endpoint != 0 ? 1 : 0;
                clusterSummary[i, 13] = cluster.Flags;
            }
            statusJson = MakeStatus(rowCount, columnCount, retained.Count, clusters.Count,
                originalRejected, invalidRows, backgroundRows, strengthRejected, protectedRows,
                missingSigma, missingEvidence, missingArrival, compensationFallback, distinctEvents,
                strength, positionRadiusM, minimumRecords, preserveCableEnds, useCompensatedCharge, phases);
        }

        private static void PreparePhase(Phase phase, double requestedRadius)
        {
            double minimumLength = Double.MaxValue, maximumLength = 0, maximumResolution = 0;
            for (int i = 0; i < phase.Rows.Count; i++)
            {
                Row row = phase.Rows[i];
                minimumLength = Math.Min(minimumLength, row.Length);
                maximumLength = Math.Max(maximumLength, row.Length);
                maximumResolution = Math.Max(maximumResolution, row.Resolution);
            }
            double allowedLengthDifference = Math.Max(0.01, 0.001 * minimumLength);
            if (maximumLength - minimumLength > allowedLengthDifference)
                throw new ArgumentException("Accepted nonzero-voltage rows in phase " + phase.Id.ToString(CultureInfo.InvariantCulture) +
                    " mix incompatible cable lengths. Filter each cable separately (allowed difference max(0.01m,0.1%)).", "parameterMatrix");
            phase.Length = minimumLength;
            if (requestedRadius == -1)
            {
                double proposed = Math.Max(Math.Max(2, Math.Min(10, 0.002 * phase.Length)), maximumResolution);
                double cap = Math.Max(0.000001, Math.Min(50, 0.05 * phase.Length));
                phase.Radius = Math.Min(proposed, cap);
                phase.RadiusCapped = proposed > cap;
            }
            else phase.Radius = requestedRadius;
            // Avoid division overflow/underflow and meaningless geometry for exceptional numeric input.
            if (phase.Radius < Math.Max(1e-12, phase.Length * 1e-15) || phase.Radius > phase.Length)
                throw new ArgumentException("The effective position radius must be at least max(1e-12m,L*1e-15) and no larger than cable length.", "positionRadiusM");
            Dictionary<EventKey, Row> representatives = new Dictionary<EventKey, Row>();
            Dictionary<double, List<double>> recordPositions = new Dictionary<double, List<double>>();
            for (int i = 0; i < phase.Rows.Count; i++)
            {
                Row row = phase.Rows[i];
                if (phase.RadiusCapped) row.Flags |= RadiusCappedFlag;
                List<double> positions;
                if (!recordPositions.TryGetValue(row.Loc, out positions))
                {
                    positions = new List<double>();
                    recordPositions.Add(row.Loc, positions);
                }
                if (row.Endpoint == 0) positions.Add(row.X);
                // Compatible row lengths may classify the same location differently.
                // An interior representative must not give endpoint rows defect evidence.
                EventKey key = new EventKey(row.Loc, row.Arrival, row.X, row.Endpoint);
                row.Key = key;
                Row existing;
                if (!representatives.TryGetValue(key, out existing) || CompareRepresentative(row, existing) < 0)
                    representatives[key] = row;
            }
            foreach (KeyValuePair<double, List<double>> item in recordPositions)
                phase.RecordCoverage.Add(item.Key, CoverageIntervals(item.Value, phase.Radius));
            foreach (KeyValuePair<EventKey, Row> item in representatives)
            {
                Row row = item.Value;
                Event point = new Event();
                point.Row = row;
                point.Cluster = -1;
                phase.Events.Add(point);
                phase.EventLookup.Add(item.Key, point);
            }
            phase.Events.Sort(CompareEvents);
        }

        private static void BuildClusters(Phase phase, List<Cluster> result, int minimumRecords)
        {
            List<Event> interior = new List<Event>();
            List<Event> start = new List<Event>();
            List<Event> end = new List<Event>();
            for (int i = 0; i < phase.Events.Count; i++)
            {
                Event point = phase.Events[i];
                if (point.Row.Endpoint == 1) start.Add(point);
                else if (point.Row.Endpoint == 2) end.Add(point);
                else interior.Add(point);
            }
            List<Cluster> local = new List<Cluster>();
            if (start.Count > 0) local.Add(NewCluster(phase, start, 1));
            if (end.Count > 0) local.Add(NewCluster(phase, end, 2));
            if (interior.Count > 0)
            {
                // Every candidate's score is the number of distinct LOCs in its fixed +/-R window.
                // A sliding window counts each LOC once, independently of its pulse multiplicity.
                int left = 0, right = 0;
                double balancedDensity = 0;
                Dictionary<double, int> active = new Dictionary<double, int>();
                List<Peak> candidates = new List<Peak>(interior.Count);
                for (int i = 0; i < interior.Count; i++)
                {
                    double x = interior[i].Row.X;
                    while (right < interior.Count && interior[right].Row.X <= x + phase.Radius)
                    {
                        balancedDensity += AddCount(active, interior[right].Row.Loc);
                        right++;
                    }
                    while (left < right && interior[left].Row.X < x - phase.Radius)
                    {
                        balancedDensity += RemoveCount(active, interior[left].Row.Loc);
                        left++;
                    }
                    Peak candidate = new Peak();
                    candidate.Index = i;
                    candidate.Support = active.Count;
                    // A record-balanced density tie-break selects complete peaks on support plateaus.
                    // Each LOC contributes less than one, irrespective of its pulse count.
                    candidate.BalancedDensity = Math.Round(balancedDensity, 10);
                    candidate.X = x;
                    candidates.Add(candidate);
                }
                candidates.Sort(delegate(Peak a, Peak b)
                {
                    int comparison = b.Support.CompareTo(a.Support);
                    if (comparison != 0) return comparison;
                    comparison = b.BalancedDensity.CompareTo(a.BalancedDensity);
                    if (comparison != 0) return comparison;
                    comparison = a.X.CompareTo(b.X);
                    return comparison != 0 ? comparison : a.Index.CompareTo(b.Index);
                });
                // A successor disjoint-set visits an event only once while assigning peak windows.
                // Already assigned members cannot produce a chain that expands a cluster indefinitely.
                int[] next = new int[interior.Count + 1];
                for (int i = 0; i < next.Length; i++) next[i] = i;
                for (int i = 0; i < candidates.Count; i++)
                {
                    Peak candidate = candidates[i];
                    if (FindNext(next, candidate.Index) != candidate.Index) continue;
                    int begin = LowerBound(interior, candidate.X - phase.Radius);
                    int stop = UpperBound(interior, candidate.X + phase.Radius);
                    List<Event> members = new List<Event>();
                    int index = FindNext(next, begin);
                    while (index < stop)
                    {
                        members.Add(interior[index]);
                        next[index] = FindNext(next, index + 1);
                        index = next[index];
                    }
                    if (members.Count > 0) local.Add(NewCluster(phase, members, 0));
                }
            }
            local.Sort(delegate(Cluster a, Cluster b)
            {
                int comparison = a.Center.CompareTo(b.Center);
                return comparison != 0 ? comparison : a.Endpoint.CompareTo(b.Endpoint);
            });
            for (int i = 0; i < local.Count; i++)
            {
                Cluster cluster = local[i];
                cluster.Id = result.Count;
                ScoreCluster(cluster, interior, minimumRecords);
                for (int j = 0; j < cluster.Events.Count; j++) cluster.Events[j].Cluster = cluster.Id;
                result.Add(cluster);
            }
            for (int i = 0; i < phase.Rows.Count; i++)
            {
                Row row = phase.Rows[i];
                Event point = phase.EventLookup[row.Key];
                Cluster cluster = result[point.Cluster];
                row.Cluster = cluster.Id;
                row.ClusterScore = cluster.Score;
                double distance = Math.Abs(row.X - cluster.Center) / phase.Radius;
                double proximity = 0.5 + 0.5 * Math.Exp(-distance * distance);
                row.MemberScore = Clamp(100 * proximity * (0.45 + 0.55 * row.SignalQuality) *
                    (0.65 + 0.35 * row.Evidence), 0, 100);
                row.DisplayScore = Math.Min(row.ClusterScore, row.MemberScore);
                cluster.MemberRows++;
                cluster.Flags |= row.Flags;
            }
        }

        private static Cluster NewCluster(Phase phase, List<Event> events, int endpoint)
        {
            Cluster cluster = new Cluster();
            cluster.Phase = phase;
            cluster.Events = events;
            cluster.Endpoint = endpoint;
            cluster.Records = GroupByRecord(events);
            List<double> positions = new List<double>();
            foreach (KeyValuePair<double, List<Event>> item in cluster.Records)
            {
                List<double> localPositions = new List<double>();
                for (int i = 0; i < item.Value.Count; i++) localPositions.Add(item.Value[i].Row.X);
                positions.Add(Median(localPositions));
            }
            cluster.Center = Median(positions);
            cluster.RecordCount = cluster.Records.Count;
            return cluster;
        }

        private static void ScoreCluster(Cluster cluster, List<Event> interior, int minimumRecords)
        {
            List<double> recordX = new List<double>(), recordQ = new List<double>();
            List<double> recordSnr = new List<double>(), recordEvidence = new List<double>();
            List<double> recordQuality = new List<double>(), recordConcentration = new List<double>();
            foreach (KeyValuePair<double, List<Event>> pair in cluster.Records)
            {
                List<double> x = new List<double>(), q = new List<double>(), snr = new List<double>();
                List<double> evidence = new List<double>(), quality = new List<double>();

                List<Event> members = pair.Value;
                for (int i = 0; i < members.Count; i++)
                {
                    Row row = members[i].Row;
                    x.Add(row.X);
                    q.Add(row.DisplayQ);
                    snr.Add(row.Snr);
                    evidence.Add(row.Evidence);
                    quality.Add(row.SignalQuality);

                    cluster.Flags |= row.Flags;
                }
                recordX.Add(Median(x));
                recordQ.Add(Median(q));
                recordSnr.Add(Median(snr));
                recordEvidence.Add(Median(evidence));
                recordQuality.Add(Median(quality));
                int globalCoverage = cluster.Phase.RecordCoverage[pair.Key];
                int localCoverage = CoverageIntervals(x, cluster.Phase.Radius);
                recordConcentration.Add(globalCoverage == 0 ? 0 : Math.Min(1, (double)localCoverage / globalCoverage));
            }
            cluster.MedianQ = Median(recordQ);
            cluster.MedianSnr = Median(recordSnr);
            cluster.MedianEvidence = Median(recordEvidence);
            if (cluster.Endpoint != 0)
            {
                // Strict cable-end entries are a display category, not automatic defect evidence.
                cluster.Score = 0;
                cluster.BackgroundSupport = 0;
                return;
            }
            double backgroundSum = 0;
            int backgroundWindows = 0;
            for (int offset = -6; offset <= 6; offset += 2)
            {
                if (offset == 0) continue;
                double center = cluster.Center + offset * cluster.Phase.Radius;
                double begin = Math.Max(0, center - cluster.Phase.Radius);
                double end = Math.Min(cluster.Phase.Length, center + cluster.Phase.Radius);
                if (end - begin < cluster.Phase.Radius) continue;
                HashSet<double> records = new HashSet<double>();
                int first = LowerBound(interior, begin);
                int stop = UpperBound(interior, end);
                for (int i = first; i < stop; i++) records.Add(interior[i].Row.Loc);
                double normalized = records.Count * (2 * cluster.Phase.Radius / (end - begin));
                backgroundSum += Math.Min(cluster.Phase.RecordCoverage.Count, normalized);
                backgroundWindows++;
            }
            cluster.BackgroundSupport = backgroundWindows == 0 ? 0 : backgroundSum / backgroundWindows;
            double prominence = Clamp((cluster.RecordCount - cluster.BackgroundSupport) / cluster.RecordCount, 0, 1);
            List<double> deviations = new List<double>();
            for (int i = 0; i < recordX.Count; i++) deviations.Add(Math.Abs(recordX[i] - cluster.Center));
            double relativeMad = Median(deviations) / (0.6 * cluster.Phase.Radius);
            double repeat = 0.5 + 0.5 * (1 - Math.Exp(-(double)cluster.RecordCount / minimumRecords));
            double compactness = 0.65 + 0.35 * Math.Exp(-relativeMad * relativeMad);
            double localContrast = 0.12 + 0.88 * prominence;
            // Local contrast already penalizes noise; global dispersion contributes at most 10%.
            double spatialConcentration = 0.9 + 0.1 * Math.Sqrt(Median(recordConcentration));
            double signal = 0.45 + 0.55 * Median(recordQuality);
            double evidenceFactor = 0.65 + 0.35 * cluster.MedianEvidence;
            cluster.Score = Clamp(100 * repeat * compactness * localContrast * spatialConcentration * signal * evidenceFactor, 0, 100);
            if (cluster.RecordCount == 1) cluster.Score = Math.Min(cluster.Score, 19.9);
            else if (cluster.RecordCount < minimumRecords) cluster.Score = Math.Min(cluster.Score, 39.9);
        }

        private static Dictionary<double, List<Event>> GroupByRecord(List<Event> events)
        {
            Dictionary<double, List<Event>> records = new Dictionary<double, List<Event>>();
            for (int i = 0; i < events.Count; i++)
            {
                Event point = events[i];
                List<Event> members;
                if (!records.TryGetValue(point.Row.Loc, out members))
                {
                    members = new List<Event>();
                    records.Add(point.Row.Loc, members);
                }
                members.Add(point);
            }
            return records;
        }

        private static string MakeStatus(int rows, int columns, int kept, int clusters,
            int originalRejected, int invalid, int background, int strengthRejected, int protectedRows,
            int missingSigma, int missingEvidence, int missingArrival, int fallback, int distinctEvents,
            double strength, double requestedRadius, int minimumRecords, bool preserveEnds,
            bool compensated, Phase[] phases)
        {
            StringBuilder json = new StringBuilder(2600);
            json.Append("{\"version\":\"1.2.1.0\",\"algorithm\":\"independent-record spatial peak evidence filter\",");
            json.Append("\"inputLimits\":{\"maximumRows\":200000,\"maximumTotalCells\":10000000},");
            json.Append("\"scoreSemantics\":\"Engineering evidence score 0..100; not probability, not vendor-equivalent, not a DL/T1576 prescribed formula. Threshold affects display only.\",");
            json.Append("\"columnMapping\":{\"phase\":0,\"locId\":1,\"voltageKv\":2,\"lengthM\":5,\"velocityMPerUs\":6,\"samplingHz\":7,\"arrivalIndex\":10,\"positionM\":13,\"rawChargePc\":15,\"compensatedChargePc\":16,\"dischargeEvidence\":22,\"sigmaPc\":28,\"modeScoreUnused\":33,\"acceptedFlag\":35},");
            json.Append("\"parameters\":{\"clusterStrength\":").Append(Number(strength));
            json.Append(",\"requestedRadiusM\":").Append(Number(requestedRadius));
            json.Append(",\"minimumIndependentRecords\":").Append(minimumRecords);
            json.Append(",\"preserveCableEnds\":").Append(preserveEnds ? "true" : "false");
            json.Append(",\"useCompensatedCharge\":").Append(compensated ? "true" : "false").Append("},");
            json.Append("\"counts\":{\"inputRows\":").Append(rows).Append(",\"columns\":").Append(columns);
            json.Append(",\"retainedRows\":").Append(kept).Append(",\"clusters\":").Append(clusters);
            json.Append(",\"originalRejectedRows\":").Append(originalRejected).Append(",\"invalidRows\":").Append(invalid);
            json.Append(",\"zeroVoltageBackgroundRows\":").Append(background).Append(",\"strengthRejectedRows\":").Append(strengthRejected);
            json.Append(",\"protectedEndpointRows\":").Append(protectedRows).Append(",\"uniqueEventLocations\":").Append(distinctEvents).Append("},");
            json.Append("\"missingEvidence\":{\"sigmaRows\":").Append(missingSigma).Append(",\"dischargeEvidenceRows\":").Append(missingEvidence);
            json.Append(",\"arrivalIndexRows\":").Append(missingArrival).Append(",\"compensatedChargeFallbackRows\":").Append(fallback).Append("},");
            json.Append("\"phases\":[");
            bool comma = false;
            for (int i = 0; i < phases.Length; i++)
            {
                Phase phase = phases[i];
                if (phase.Rows.Count == 0) continue;
                if (comma) json.Append(',');
                comma = true;
                json.Append("{\"phase\":").Append(phase.Id).Append(",\"lengthM\":").Append(Number(phase.Length));
                json.Append(",\"radiusM\":").Append(Number(phase.Radius)).Append(",\"independentRecords\":").Append(phase.RecordCoverage.Count);
                json.Append(",\"automaticRadiusCapped\":").Append(phase.RadiusCapped ? "true" : "false").Append('}');
            }
            json.Append("],\"autoRadiusRule\":\"min(max(2,min(10,0.002L),max(v*1e6/fs)),max(1e-6,min(50,0.05L))); invalid sampling metadata contributes no resolution floor\",");
            json.Append("\"deduplication\":\"Within each phase and endpoint category, identical (LOC,arrivalIndex,position) events vote once. Endpoint and interior rows remain separate when compatible row lengths differ. Each LOC contributes one median to cluster evidence; all original member rows remain eligible for output.\",");
            json.Append("\"reasonCodes\":{\"1\":\"retained\",\"2\":\"strict endpoint display protection\",\"3\":\"insufficient score\",\"-1\":\"originally rejected\",\"-2\":\"invalid fields\",\"-3\":\"zero voltage background\"},");
            json.Append("\"flagBits\":{\"1\":\"strict endpoint\",\"2\":\"missing sigma\",\"4\":\"missing discharge evidence\",\"8\":\"missing arrival index\",\"16\":\"compensated charge fallback\",\"32\":\"automatic radius capped\"},");
            json.Append("\"warnings\":[\"Endpoint protection is display-only, with cluster/display score zero; it is not defect confirmation. Tolerance=max(1e-9m,1e-12*rowLength).\",");
            json.Append("\"Missing sigma or evidence uses neutral quality 0.5; evidence zero is a valid zero, not a missing sentinel. Missing arrival uses -1 plus position for event deduplication.\",");
            json.Append("\"Raw Q/sigma, not compensated Q/sigma, determines signal evidence. Compensated Q affects summary display charge only. Mode-dependent column33 is unused.\",");
            json.Append("\"Spatial filtering alone cannot determine whether a source is a true defect; sparse true sources can receive low scores. Zero voltage rows are excluded from support.\"]}");
            return json.ToString();
        }

        private static int CompareRepresentative(Row a, Row b)
        {
            int result = a.RawQ.CompareTo(b.RawQ);
            if (result != 0) return result;
            result = a.SignalQuality.CompareTo(b.SignalQuality);
            if (result != 0) return result;
            result = a.Evidence.CompareTo(b.Evidence);
            if (result != 0) return result;
            // Raw evidence must decide the representative before display-only charge.
            result = a.Snr.CompareTo(b.Snr);
            if (result != 0) return result;
            result = a.DisplayQ.CompareTo(b.DisplayQ);
            if (result != 0) return result;
            return a.Flags.CompareTo(b.Flags);
        }

        private static int CompareEvents(Event a, Event b)
        {
            int result = a.Row.X.CompareTo(b.Row.X);
            if (result != 0) return result;
            result = a.Row.Loc.CompareTo(b.Row.Loc);
            if (result != 0) return result;
            result = a.Row.Arrival.CompareTo(b.Row.Arrival);
            return result != 0 ? result : a.Row.Endpoint.CompareTo(b.Row.Endpoint);
        }

        private static int FindNext(int[] next, int index)
        {
            int root = index;
            while (next[root] != root) root = next[root];
            while (next[index] != index)
            {
                int parent = next[index];
                next[index] = root;
                index = parent;
            }
            return root;
        }

        private static int LowerBound(List<Event> events, double x)
        {
            int low = 0, high = events.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (events[middle].Row.X < x) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        private static int UpperBound(List<Event> events, double x)
        {
            int low = 0, high = events.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (events[middle].Row.X <= x) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        private static double AddCount(Dictionary<double, int> counts, double key)
        {
            int count;
            counts.TryGetValue(key, out count);
            counts[key] = count + 1;
            return (double)(count + 1) / (count + 2) - (double)count / (count + 1);
        }

        private static double RemoveCount(Dictionary<double, int> counts, double key)
        {
            int count = counts[key] - 1;
            if (count == 0) counts.Remove(key);
            else counts[key] = count;
            return (double)count / (count + 1) - (double)(count + 1) / (count + 2);
        }

        private static double SamplingResolution(double velocity, double sampling)
        {
            // Multiply first when safe to preserve subnormal ratios; otherwise divide first.
            double resolution = velocity <= Double.MaxValue / 1000000.0
                ? velocity * 1000000.0 / sampling : (velocity / sampling) * 1000000.0;
            return Double.IsPositiveInfinity(resolution) ? Double.MaxValue : resolution;
        }

        private static int CoverageIntervals(List<double> positions, double radius)
        {
            if (positions.Count == 0) return 0;
            positions.Sort();
            // Greedy minimum coverage by intervals of width 2R has no arbitrary grid origin.
            // Subtract before comparing so translating all positions preserves concentration.
            int count = 1;
            double start = positions[0];
            for (int i = 1; i < positions.Count; i++)
                if ((positions[i] - start) * 0.5 > radius)
                {
                    count++;
                    start = positions[i];
                }
            return count;
        }
        private static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        private static double Clamp(double value, double minimum, double maximum) { return Math.Max(minimum, Math.Min(maximum, value)); }
        private static string Number(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
        private static double Median(List<double> values)
        {
            if (values.Count == 0) return 0;
            values.Sort();
            int middle = values.Count / 2;
            if ((values.Count & 1) == 1) return values[middle];
            double lower = values[middle - 1], upper = values[middle];
            // Same-sign subtraction cannot overflow and preserves equal positive subnormals.
            return lower >= 0 || upper <= 0
                ? lower + (upper - lower) * 0.5 : lower * 0.5 + upper * 0.5;
        }

        private sealed class Row
        {
            internal int Source, Phase, Cluster, Reason, Flags, Endpoint;
            internal bool Valid;
            internal double Loc, Length, X, RawQ, DisplayQ, Snr, SignalQuality, Evidence, Arrival, Resolution;
            internal double ClusterScore, MemberScore, DisplayScore;
            internal EventKey Key;
        }

        private sealed class Event
        {
            internal Row Row;
            internal int Cluster;
        }

        private sealed class Phase
        {
            internal readonly int Id;
            internal readonly List<Row> Rows = new List<Row>();
            internal readonly List<Event> Events = new List<Event>();
            internal readonly Dictionary<EventKey, Event> EventLookup = new Dictionary<EventKey, Event>();
            internal readonly Dictionary<double, int> RecordCoverage = new Dictionary<double, int>();
            internal double Length, Radius;
            internal bool RadiusCapped;
            internal Phase(int id) { Id = id; }
        }

        private sealed class Cluster
        {
            internal int Id, Endpoint, MemberRows, RecordCount, Flags;
            internal Phase Phase;
            internal List<Event> Events;
            internal Dictionary<double, List<Event>> Records;
            internal double Center, Score, BackgroundSupport, MedianQ, MedianSnr, MedianEvidence;
        }

        private sealed class Peak
        {
            internal int Index, Support;
            internal double X, BalancedDensity;
        }

        private struct EventKey : IEquatable<EventKey>
        {
            private readonly double loc, arrival, position;
            private readonly int endpoint;
            internal EventKey(double locValue, double arrivalValue, double positionValue, int endpointValue)
            { loc = locValue; arrival = arrivalValue; position = positionValue; endpoint = endpointValue; }
            public bool Equals(EventKey other) { return loc == other.loc && arrival == other.arrival && position == other.position && endpoint == other.endpoint; }
            public override bool Equals(object other) { return other is EventKey && Equals((EventKey)other); }
            public override int GetHashCode()
            {
                unchecked { return (((loc.GetHashCode() * 397) ^ arrival.GetHashCode()) * 397 ^ position.GetHashCode()) * 397 ^ endpoint; }
            }
        }
    }
}
