using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HVX.UnifiedSpectrum
{
    internal sealed class SpectrumPoint
    {
        public double X, Y, Z;
        public int Phase;
        public int[] SourceRows;
        // A colour statistic, never a replacement for signed charge or source identity.
        public int DensityCount = 1;
    }

    internal sealed class SpectrumView
    {
        public List<SpectrumPoint> Points = new List<SpectrumPoint>();
        public List<SpectrumOrderGroup> OrderGroups = new List<SpectrumOrderGroup>();
        public string XKey, YKey, ZKey;
        public bool Is3D, IsHistogram, IsHeatmap, IsOrderPlot;
        public double XMin, XMax, YMin, YMax, ZMin, ZMax;
        public string Warning = string.Empty;
        public int DensityMaximum = 1;
        public SpectrumDensityBins DensityBins;
        public List<ChargeCountCell> ChargeCounts = new List<ChargeCountCell>();
    }

    /// <summary>
    /// Pure matrix adapter. Never changes the caller's matrix or accepts a rejected
    /// row. Matrix columns are zero based. Column 22 is not a probability port.
    /// Phase filter: -1 combines visible ABC; 0 selects only phase-code 0 (ALL);
    /// 1..3 selects A/B/C. The ALL source is a fallback, never added to ABC.
    /// Units: L=m, Q=pC, U=kV, F=Hz, T=ns, PHI=degrees.
    /// </summary>
    internal static class SpectrumData
    {
        // Competition's voltage/phase spectra preserve the same source-row,
        // phase visibility, acceptance and charge contracts as the normal plots.
        internal static SpectrumView BuildCompetition(double[,] matrix,int mode,bool[] visible,int phase,bool accepted,int phaseBins,int chargeBins,double length,bool[] mask,bool compensated)
        {
            int baseMode=mode==7||mode==8?2:mode;
            var view=Build(matrix,baseMode,visible,phase,accepted,null,100,phaseBins,chargeBins,length,mask,null,null,compensated);
            if(mode!=7&&mode!=8)return view;
            view.Points.RemoveAll(p=>!Finite(matrix[p.SourceRows[0],2])||matrix[p.SourceRows[0],2]<0);
            foreach(var p in view.Points)p.Z=matrix[p.SourceRows[0],2];
            if(mode==8)
            {
                int bins=Math.Max(1,Math.Min(360,phaseBins));
                view.Points=view.Points.GroupBy(p=>new {p.Phase,p.Z,Bin=Math.Min(bins-1,(int)(p.X/360*bins))}).Select(group=>new SpectrumPoint{
                    Phase=group.Key.Phase,X=(group.Key.Bin+.5)*360/bins,Y=group.Sum(p=>p.SourceRows.Length),Z=group.Key.Z,
                    SourceRows=group.SelectMany(p=>p.SourceRows).Distinct().ToArray()}).ToList();
                view.YKey="N";view.IsHistogram=true;
            }
            view.XKey="PHI";view.ZKey="U";view.Is3D=true;SetRanges(view,length);return view;
        }
        private sealed class Pulse
        {
            internal int Row, Phase;
            internal double L, Q, U, Phi, F, T, Order, RecordId;
        }

        private sealed class Bucket
        {
            internal double X, Z;
            internal int Phase, Count;
            internal readonly List<int> Rows = new List<int>();
        }

        internal static SpectrumView Build(double[,] matrix, int mode, bool[] visible,
            int phaseFilter, bool acceptedOnly, HashSet<int> deleted, int positionBins,
            int phaseBins, int chargeBins, double cableLength, bool[] reviewVisibility = null, double[] reviewCountRange = null, Func<int,double,double> positionProjection = null, bool useCompensatedCharge = false, bool capacitorMode = false)
        {
            if (mode < 0 || mode > 12) throw new ArgumentOutOfRangeException("mode");
            if (phaseFilter < -1 || phaseFilter > 3) throw new ArgumentOutOfRangeException("phaseFilter");
            if (visible != null && visible.Length != 3)
                throw new ArgumentException("visible must contain A/B/C visibility flags.", "visible");
            if (matrix != null && matrix.GetLength(1) < 36)
                throw new ArgumentException("The location matrix requires at least 36 columns.", "matrix");

            SpectrumView view = CreateView(mode);
            int rows = matrix == null ? 0 : matrix.GetLength(0);
            if (reviewVisibility != null && reviewVisibility.Length != rows)
                throw new ArgumentException("Review visibility must contain one flag per source row.", "reviewVisibility");
            bool needOrder = mode == 9 || mode == 10;
            bool derivedOrder = false;
            double[] orders = needOrder ? ReadOrders(matrix, ref derivedOrder) : null;
            bool anyAbcSource = false, anyAllSource = false;
            for (int r = 0; r < rows; r++)
            {
                int phase = DecodePhase(matrix[r, 0]);
                if (phase == 0) anyAllSource = true;
                // Do not let an ALL summary reintroduce a hidden channel.
                if (phase >= 1) anyAbcSource = true;
            }

            List<Pulse> pulses = new List<Pulse>();
            int invalidPhase = 0;
            for (int r = 0; r < rows; r++)
            {
                if (deleted != null && deleted.Contains(r)) continue;
                if (reviewVisibility != null && !reviewVisibility[r]) continue;
                if (acceptedOnly && matrix[r, 35] != 1.0) continue;
                int phase = DecodePhase(matrix[r, 0]);
                if (phase < 0) { invalidPhase++; continue; }
                if (phaseFilter == -1)
                {
                    if (phase == 0 && anyAbcSource) continue;
                    if (phase >= 1 && !IsVisible(phase, visible)) continue;
                    // If all three channels are hidden, ALL must not reappear.
                    if (phase == 0 && visible != null && !visible[0] && !visible[1] && !visible[2]) continue;
                }
                else if (phase != phaseFilter || !IsVisible(phase, visible)) continue;
                pulses.Add(new Pulse {
                    Row = r, Phase = phase, RecordId = matrix[r, 1], L = positionProjection==null?matrix[r,13]:positionProjection(r,matrix[r,13]),
                    // Task11 order statistics prefer compensated Q (column 16).
                    // Other modes retain their existing raw-Q contract.
                    Q = (needOrder||useCompensatedCharge) && Finite(matrix[r, 16]) ? matrix[r, 16] : matrix[r, 15],
                    U = matrix[r, 2], Phi = NormalizePhase(matrix[r, 3]),
                    F = matrix[r, 24], T = matrix[r, 23],
                    Order = needOrder ? orders[r] : double.NaN
                });
            }

            double length = Finite(cableLength) && cableLength > 0.0 ? cableLength : 0.0;
            if (length <= 0.0 && matrix != null)
                for (int r = 0; r < rows; r++)
                    if (Finite(matrix[r, 5]) && matrix[r, 5] > length) length = matrix[r, 5];
            if (length <= 0.0)
                foreach (Pulse pulse in pulses) if (Finite(pulse.L) && pulse.L > length) length = pulse.L;
            int pBins = positionBins <= 0 ? RecommendBins(pulses.Count) : Clamp(positionBins, 1, 100);
            int phiBins = phaseBins <= 0 ? 72 : Clamp(phaseBins, 1, 360);
            int qBins = chargeBins <= 0 ? 50 : Clamp(chargeBins, 1, 240);

            if(capacitorMode){length=0;foreach(var pulse in pulses)pulse.L=0;}
            switch (mode)
            {
                case 0: case 2: case 4: case 5: case 7: case 12:
                    BuildScatter(view, pulses, mode, length); break;
                case 1: case 3:
                    BuildHistogram(view, pulses, mode == 1, mode == 1 ? pBins : phiBins,
                        capacitorMode&&mode==1?1:length, phaseFilter, mode == 1 ? reviewCountRange : null); break;
                case 6: case 8:
                    BuildCountSurface(view, pulses, mode == 6, pBins, phiBins, capacitorMode?1:length, phaseFilter); break;
                case 9: case 10:
                    BuildOrder(view, pulses, mode == 9); break;
                case 11:
                    BuildPrpd(view, pulses, phiBins, qBins); break;
            }
            if(mode==5)view.ChargeCounts=ChargeCountBinning.Build(view,qBins);
            if(capacitorMode) {
                if(view.XKey=="L")foreach(var point in view.Points)point.X=0;
                AppendWarning(view,"Capacitor PD: no localization; 0 m is a display placeholder, not a measured fault position.");
            }
            if (phaseFilter == -1 && anyAbcSource && anyAllSource)
                AppendWarning(view, "ALL combines visible A/B/C rows; phase-code 0 summary rows are not added again.");
            if (derivedOrder)
                AppendWarning(view, "n uses positive column 36 where supplied; otherwise each phase's record IDs are numbered from 0 in first matrix appearance order, not verified project chronology.");
            if (invalidPhase > 0) AppendWarning(view, "Rows with invalid phase codes were excluded: " + invalidPhase.ToString(CultureInfo.InvariantCulture) + ".");
            bool anyData = false;
            foreach (SpectrumPoint point in view.Points)
                if (point.SourceRows.Length > 0) { anyData = true; break; }
            if (!anyData) AppendWarning(view, "No valid data for this display mode and selection.");
            SetRanges(view, length);
            if (mode == 12) SpectrumDensity.AssignLocationPrpd(view, pBins, phiBins, qBins);
            return view;
        }

        private static SpectrumView CreateView(int mode)
        {
            SpectrumView v = new SpectrumView();
            v.XKey = mode == 0 || mode == 1 || mode == 5 || mode == 6 || mode == 7 || mode == 8 || mode == 12
                ? "L" : mode == 4 ? "F" : mode == 9 || mode == 10 ? "n" : "PHI";
            v.YKey = mode == 1 || mode == 3 || mode == 6 || mode == 8 || mode == 10
                ? "N" : mode == 4 ? "T" : "Q";
            v.ZKey = mode == 5 || mode == 6 ? "U" : mode == 7 || mode == 8 || mode == 12 ? "PHI"
                : mode == 9 || mode == 10 ? "U" : mode == 11 ? "Density" : string.Empty;
            v.Is3D = mode >= 5 && mode <= 8 || mode == 12;
            v.IsHistogram = mode == 1 || mode == 3 || mode == 6 || mode == 8;
            v.IsHeatmap = mode == 11;
            v.IsOrderPlot = mode == 9 || mode == 10;
            return v;
        }

        private static void BuildScatter(SpectrumView view, List<Pulse> pulses, int mode, double length)
        {
            foreach (Pulse p in pulses)
            {
                double x, y, z = 0.0;
                if (mode == 4) { x = p.F; y = p.T; if (!(x > 0.0) || !(y > 0.0)) continue; }
                else
                {
                    x = mode == 2 ? p.Phi : p.L;
                    y = mode == 2 || mode == 7 || mode == 12 ? p.Q : Math.Abs(p.Q);
                    if (mode != 2 && !ValidPosition(p.L, length)) continue;
                    if (mode == 5) z = p.U;
                    if (mode == 7 || mode == 12) z = p.Phi;
                }
                if (!Finite(x) || !Finite(y) || !Finite(z)) continue;
                view.Points.Add(new SpectrumPoint { X = x, Y = y, Z = z, Phase = p.Phase, SourceRows = new[] { p.Row } });
            }
        }

        private static void BuildHistogram(SpectrumView view, List<Pulse> pulses, bool position,
            int bins, double length, int phaseFilter, double[] countRange)
        {
            double limit = position ? length : 360.0;
            if (!(limit > 0.0)) return;
            int displayPhase = phaseFilter > 0 ? phaseFilter : 0;
            List<Pulse>[] members = new List<Pulse>[bins];
            for (int b = 0; b < bins; b++) members[b] = new List<Pulse>();
            foreach (Pulse p in pulses)
            {
                double x = position ? p.L : p.Phi;
                if (!Finite(x) || position && !ValidPosition(x, length)) continue;
                members[Bin(x, 0.0, limit, bins)].Add(p);
            }
            for (int b = 0; b < bins; b++)
            {
                int[] counts = new int[4];
                foreach (Pulse p in members[b]) counts[p.Phase]++;
                var includedRows = new List<int>();
                foreach (Pulse p in members[b])
                    if (countRange == null || counts[p.Phase] >= countRange[0] && counts[p.Phase] <= countRange[1]) includedRows.Add(p.Row);
                view.Points.Add(new SpectrumPoint { X = ((b + 0.5) / bins) * limit,
                    Y = includedRows.Count, Z = 0.0, Phase = displayPhase, SourceRows = includedRows.ToArray() });
            }
        }

        private static void BuildCountSurface(SpectrumView view, List<Pulse> pulses, bool voltage,
            int positionBins, int phaseBins, double length, int phaseFilter)
        {
            if (!(length > 0.0)) return;
            Dictionary<string, Bucket> buckets = new Dictionary<string, Bucket>(StringComparer.Ordinal);
            int phase = phaseFilter > 0 ? phaseFilter : 0;
            foreach (Pulse p in pulses)
            {
                if (!ValidPosition(p.L, length)) continue;
                double z = voltage ? p.U : p.Phi;
                if (!Finite(z)) continue;
                int xbin = Bin(p.L, 0.0, length, positionBins);
                // The existing PRPS adapter uses 0.1 kV voltage layers.
                z = voltage ? Math.Round(z, 1, MidpointRounding.AwayFromZero)
                    : (Bin(z, 0.0, 360.0, phaseBins) + 0.5) * 360.0 / phaseBins;
                string key = xbin.ToString(CultureInfo.InvariantCulture) + ":" + z.ToString("R", CultureInfo.InvariantCulture);
                Bucket bucket;
                if (!buckets.TryGetValue(key, out bucket))
                {
                    bucket = new Bucket { X = ((xbin + 0.5) / positionBins) * length, Z = z, Phase = phase };
                    buckets.Add(key, bucket);
                }
                bucket.Rows.Add(p.Row); bucket.Count++;
            }
            AddCountBuckets(view, buckets);
        }

        private static void BuildOrder(SpectrumView view, List<Pulse> pulses, bool charge)
        {
            var groups = new Dictionary<string, SpectrumOrderGroup>(StringComparer.Ordinal);
            var recordVoltages = new Dictionary<string, double>(StringComparer.Ordinal);
            bool voltageConflict = false;
            foreach (Pulse p in pulses)
            {
                if (!Finite(p.RecordId) || !Finite(p.Order) || p.Order < 0.0 ||
                    !Finite(p.U) || charge && !Finite(p.Q)) continue;
                string record = p.Phase.ToString(CultureInfo.InvariantCulture) + ":" + p.RecordId.ToString("R", CultureInfo.InvariantCulture);
                double recordVoltage;
                if (recordVoltages.TryGetValue(record, out recordVoltage))
                    voltageConflict |= recordVoltage != p.U;
                else recordVoltages.Add(record, p.U);
                // Never average different voltages, phases, or file records into one box.
                string key = record + ":" + p.Order.ToString("R", CultureInfo.InvariantCulture) + ":" + p.U.ToString("R", CultureInfo.InvariantCulture);
                SpectrumOrderGroup group;
                if (!groups.TryGetValue(key, out group))
                {
                    group = new SpectrumOrderGroup { Order = p.Order, Phase = p.Phase, RecordId = p.RecordId, Voltage = p.U };
                    groups.Add(key, group);
                }
                // N counts pulse rows independently of a missing charge measurement.
                group.Add(p.Row, p.Q);
            }
            view.OrderGroups.AddRange(groups.Values);
            view.OrderGroups.Sort(delegate(SpectrumOrderGroup a, SpectrumOrderGroup b)
            {
                int c = a.Phase.CompareTo(b.Phase); if (c != 0) return c;
                c = a.Order.CompareTo(b.Order); if (c != 0) return c;
                c = a.RecordId.CompareTo(b.RecordId); return c != 0 ? c : a.Voltage.CompareTo(b.Voltage);
            });
            foreach (SpectrumOrderGroup group in view.OrderGroups)
            {
                group.Complete();
                view.Points.Add(new SpectrumPoint { X = group.Order, Y = charge ? group.Maximum : group.Count,
                    Z = group.Voltage, Phase = group.Phase, SourceRows = group.SourceRows });
            }
            if (voltageConflict) AppendWarning(view, "One phase/record ID contains different voltages; its voltage groups remain separate (no voltage averaging).");
        }

        private static void BuildPrpd(SpectrumView view, List<Pulse> pulses, int phaseBins, int chargeBins)
        {
            double maximumCharge = 0.0;
            foreach (Pulse p in pulses)
                if (Finite(p.Phi) && Finite(p.Q)) maximumCharge = Math.Max(maximumCharge, Math.Abs(p.Q));
            if (!(maximumCharge > 0.0)) maximumCharge = 1.0;
            Dictionary<string, Bucket> buckets = new Dictionary<string, Bucket>(StringComparer.Ordinal);
            foreach (Pulse p in pulses)
            {
                if (!Finite(p.Phi) || !Finite(p.Q)) continue;
                int x = Bin(p.Phi, 0.0, 360.0, phaseBins);
                // Divide before offsetting to avoid 2 * maximumCharge overflow.
                int y = SpectrumDensity.ChargeIndex(p.Q, maximumCharge, chargeBins);
                string key = x.ToString(CultureInfo.InvariantCulture) + ":" + y.ToString(CultureInfo.InvariantCulture);
                Bucket bucket;
                if (!buckets.TryGetValue(key, out bucket))
                {
                    bucket = new Bucket { X = (x + 0.5) * 360.0 / phaseBins,
                        Z = ((y + 0.5) / chargeBins * 2.0 - 1.0) * maximumCharge, Phase = 0 };
                    buckets.Add(key, bucket);
                }
                bucket.Rows.Add(p.Row); bucket.Count++;
            }
            foreach (Bucket b in buckets.Values)
            {
                view.Points.Add(new SpectrumPoint { X = b.X, Y = b.Z, Z = b.Count,
                    Phase = b.Phase, SourceRows = b.Rows.ToArray(), DensityCount = b.Count });
                view.DensityMaximum = Math.Max(view.DensityMaximum, b.Count);
            }
            view.DensityBins = SpectrumDensity.DescribePrpd(phaseBins, chargeBins, maximumCharge);
            view.YMin = -maximumCharge; view.YMax = maximumCharge;
            view.Points.Sort(ComparePoints);
        }

        private static void AddCountBuckets(SpectrumView view, Dictionary<string, Bucket> buckets)
        {
            foreach (Bucket b in buckets.Values)
                view.Points.Add(new SpectrumPoint { X = b.X, Y = b.Count, Z = b.Z,
                    Phase = b.Phase, SourceRows = b.Rows.ToArray() });
            view.Points.Sort(ComparePoints);
        }

        private static int ComparePoints(SpectrumPoint a, SpectrumPoint b)
        {
            int n = a.Z.CompareTo(b.Z); return n != 0 ? n : a.X.CompareTo(b.X);
        }

        private static double[] ReadOrders(double[,] matrix, ref bool derived)
        {
            int rows = matrix == null ? 0 : matrix.GetLength(0);
            int columns = matrix == null ? 0 : matrix.GetLength(1);
            double[] orders = new double[rows];
            var byPhase = new Dictionary<int, Dictionary<double, double>>();
            // Derive on the complete input, before visibility/selection/deletion,
            // so changing the display does not renumber the same pulse.
            for (int r = 0; r < rows; r++)
            {
                double id = matrix[r, 1];
                int phase = DecodePhase(matrix[r, 0]);
                Dictionary<double, double> byId;
                if (!byPhase.TryGetValue(phase, out byId))
                { byId = new Dictionary<double, double>(); byPhase.Add(phase, byId); }
                if (Finite(id) && !byId.ContainsKey(id)) byId.Add(id, byId.Count);
                double explicitOrder = columns > 36 ? matrix[r, 36] : double.NaN;
                if (Finite(explicitOrder) && explicitOrder > 0.0 && explicitOrder == Math.Floor(explicitOrder))
                    orders[r] = explicitOrder;
                else
                {
                    derived = true;
                    orders[r] = Finite(id) ? byId[id] : double.NaN;
                }
            }
            return orders;
        }

        private static void SetRanges(SpectrumView view, double cableLength)
        {
            double xmin = double.PositiveInfinity, xmax = double.NegativeInfinity;
            double ymin = 0.0, ymax = 0.0, zmin = double.PositiveInfinity, zmax = double.NegativeInfinity;
            foreach (SpectrumPoint p in view.Points)
            {
                xmin = Math.Min(xmin, p.X); xmax = Math.Max(xmax, p.X);
                ymin = Math.Min(ymin, p.Y); ymax = Math.Max(ymax, p.Y);
                zmin = Math.Min(zmin, p.Z); zmax = Math.Max(zmax, p.Z);
            }
            if (view.XKey == "PHI") { xmin = 0.0; xmax = 360.0; }
            else if (view.XKey == "L") { xmin = 0.0; xmax = cableLength > 0.0 ? cableLength : 1.0; }
            else if (!Finite(xmin) || !Finite(xmax)) { xmin = 0.0; xmax = 1.0; }
            if (view.IsOrderPlot) { xmin = Math.Min(0.0, xmin) - 0.55; xmax += 0.55; }
            if (!(xmax > xmin)) { xmin = Math.Min(0.0, xmin); xmax = xmax > xmin ? xmax : xmin + 1.0; }
            if (view.IsHeatmap) { ymin = view.YMin; ymax = view.YMax; }
            if (!(ymax > ymin)) ymax = ymin + 1.0;
            if (view.ZKey == "PHI") { zmin = 0.0; zmax = 360.0; }
            else if (view.ZKey == "L") { zmin = 0.0; zmax = cableLength > 0.0 ? cableLength : 1.0; }
            else if (!Finite(zmin) || !Finite(zmax)) { zmin = 0.0; zmax = 1.0; }
            if (view.IsOrderPlot) { zmin = Math.Min(0.0, zmin); zmax = Math.Max(0.0, zmax); }
            if (!(zmax > zmin)) { zmin = Math.Min(0.0, zmin); zmax = zmax > zmin ? zmax : zmin + 1.0; }
            view.XMin = xmin; view.XMax = xmax; view.YMin = ymin; view.YMax = ymax; view.ZMin = zmin; view.ZMax = zmax;
        }

        private static void AppendWarning(SpectrumView view, string text)
        {
            view.Warning = view.Warning.Length == 0 ? text : view.Warning + " " + text;
        }

        private static bool ValidPosition(double position, double length)
        {
            return Finite(position) && position >= 0.0 && (!(length > 0.0) || position <= length);
        }
        private static bool IsVisible(int phase, bool[] visible) { return phase == 0 || visible == null || visible[phase - 1]; }
        private static int DecodePhase(double p) { return Finite(p) && p >= 0.0 && p <= 3.0 && p == Math.Floor(p) ? (int)p : -1; }
        private static double NormalizePhase(double p) { return Finite(p) ? ((p % 360.0) + 360.0) % 360.0 : double.NaN; }
        private static int Bin(double value, double min, double max, int bins)
        {
            if (value <= min) return 0;
            if (value >= max) return bins - 1;
            return Clamp((int)Math.Floor((value - min) / (max - min) * bins), 0, bins - 1);
        }
        private static int RecommendBins(int count) { return Clamp((int)Math.Ceiling(2.5 * Math.Sqrt(Math.Max(1, count))), 4, 100); }
        private static int Clamp(int value, int min, int max) { return Math.Max(min, Math.Min(max, value)); }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    }
}
