using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;

namespace HVX.UnifiedSpectrum
{
    public partial class UnifiedSpectrumControl
    {
        // A population summary, not a count of points/cells/bars. It deliberately
        // does not depend on the active mode's coordinate requirements or zoom.
        // ClearDataViews invalidates it whenever row selection or measurements change.
        bool summaryDirty = true;
        int[] summaryRows = new int[0];
        int summaryChargeCount, summaryComputationCount, summaryLocationCount, summaryMissingLocationCount, summaryOutOfRangeLocationCount;
        double summaryMaximumCharge = double.NaN, summaryMeanCharge = double.NaN;
        string summarySuffix;
        bool summarySuffixEnglish;
        int summarySuffixUnit;
        bool summarySuffixLocation;

        bool LocationCoordinatesRequired { get { return mode==0||mode==1||mode==5||mode==6||mode==7||mode==8||mode==12; } }
        [Browsable(false), Description("Common-population rows with a finite, in-cable displayed location. Capacitor placeholders are never measured locations.")]
        public int SummaryValidLocationCount { get { EnsureSummaryStatistics(); return summaryLocationCount; } }
        [Browsable(false)] public int SummaryMissingLocationCount { get { EnsureSummaryStatistics(); return summaryMissingLocationCount; } }
        [Browsable(false)] public int SummaryOutOfRangeLocationCount { get { EnsureSummaryStatistics(); return summaryOutOfRangeLocationCount; } }

        [Browsable(false), Description("Source-row population after acceptance, phase visibility, review filters and view deletion. Independent of chart coordinates, bins and zoom.")]
        public int SummaryTotalDischargeCount { get { EnsureSummaryStatistics(); return summaryRows.Length; } }
        [Browsable(false), Description("Rows in the common population with a finite original charge in zero-based column 15.")]
        public int SummaryValidChargeCount { get { EnsureSummaryStatistics(); return summaryChargeCount; } }
        [Browsable(false)]
        public int SummaryMissingChargeCount { get { EnsureSummaryStatistics(); return summaryRows.Length - summaryChargeCount; } }
        [Browsable(false), Description("Maximum absolute original charge (column 15), in pC. NaN means no finite charge is available.")]
        public double SummaryMaximumChargePc { get { EnsureSummaryStatistics(); return summaryMaximumCharge; } }
        [Browsable(false), Description("Mean absolute original charge (column 15), in pC, over finite charges only. NaN means unavailable.")]
        public double SummaryMeanChargePc { get { EnsureSummaryStatistics(); return summaryMeanCharge; } }
        [Browsable(false)]
        public int[] SummarySourceRowIndices { get { EnsureSummaryStatistics(); return (int[])summaryRows.Clone(); } }
        [Browsable(false)]
        public int SummaryComputationCount { get { return summaryComputationCount; } }

        [Browsable(false), Description("Common-population title suffix. Original absolute Q is used for all modes; existing n-Q-U box statistics retain their compensated-Q contract.")]
        public string SummaryTitleSuffix
        {
            get
            {
                EnsureSummaryStatistics();
                if (summarySuffix == null || summarySuffixEnglish != english || summarySuffixUnit != chargeUnit || summarySuffixLocation != LocationCoordinatesRequired)
                {
                    string total = summaryRows.Length.ToString(CultureInfo.InvariantCulture);
                    string maximum = FormatSummaryCharge(summaryMaximumCharge);
                    string mean = FormatSummaryCharge(summaryMeanCharge);
                    summarySuffix = english
                        ? " ("+L("Total discharges","总放电次数")+": " + total + ", "+L("max charge","最大放电量")+": " + maximum + ", "+L("mean charge","平均放电量")+": " + mean
                        : "（总放电次数：" + total + "次，最大放电量：" + maximum + "，平均放电量：" + mean;
                    // Do not imply that missing amplitudes were zero measurements.
                    if (summaryChargeCount < summaryRows.Length)
                        summarySuffix += (L(", valid charge: ","，有效放电量：")) +
                            summaryChargeCount.ToString(CultureInfo.InvariantCulture) + "/" + total;
                    if(LocationCoordinatesRequired&&!capacitorStatisticsMode)
                        summarySuffix+=L(", valid locations: ","，有效定位：")+summaryLocationCount.ToString(CultureInfo.InvariantCulture)+"/"+total;
                    summarySuffix += L(")","）");
                    summarySuffixEnglish = english;
                    summarySuffixUnit = chargeUnit;
                    summarySuffixLocation = LocationCoordinatesRequired;
                }
                return summarySuffix;
            }
        }

        void InvalidateSummaryStatistics()
        { summaryDirty = true; summarySuffix = null; }

        void EnsureSummaryStatistics()
        {
            if (!summaryDirty) return;
            var rows = new List<int>();
            int finiteCharges = 0;
            int located=0,missingLocation=0,outOfRangeLocation=0;
            double maximum = double.NaN, mean = 0;
            double locationLength=metadataGeometryDirty?CableLength():currentLength;
            // Match the adapter's fallback when no project/phase length exists.
            if(!(locationLength>0))for(int r=0;r<source.GetLength(0);r++)
                if(Finite(source[r,5])&&source[r,5]>locationLength)locationLength=source[r,5];
            bool anyAbcSource = false;
            for (int r = 0; r < source.GetLength(0); r++)
            {
                double p = source[r, 0];
                if (Finite(p) && p >= 1 && p <= 3 && p == Math.Floor(p)) { anyAbcSource = true; break; }
            }
            bool anyPhaseVisible = visible[0] || visible[1] || visible[2];
            for (int r = 0; r < source.GetLength(0); r++)
            {
                if (deleted.Contains(r) || !IsReviewRowVisible(r) || !IsRegionStatisticsRow(r) || acceptedOnly && source[r, 35] != 1) continue;
                double value = source[r, 0];
                if (!Finite(value) || value < 0 || value > 3 || value != Math.Floor(value)) continue;
                int phase = (int)value;
                // Match SpectrumData's ABC / ALL-source fallback exactly. ALL is
                // not added to ABC, including when ABC is hidden or filtered out.
                if (phaseFilter == -1)
                {
                    if (phase == 0 && (anyAbcSource || !anyPhaseVisible)) continue;
                    if (phase > 0 && !visible[phase - 1]) continue;
                }
                else if (phase != phaseFilter || phase > 0 && !visible[phase - 1]) continue;
                rows.Add(r); // Identity is the source row, never the reusable PDmap ID.
                double position=reviewPositionProjection==null?source[r,13]:reviewPositionProjection(r,source[r,13]);
                if(capacitorStatisticsMode||!Finite(position))missingLocation++;
                else if(position<0||locationLength>0&&position>locationLength)outOfRangeLocation++;
                else located++;
                double charge = PresentedCharge(r);
                if (!Finite(charge)) continue;
                charge = Math.Abs(charge);
                finiteCharges++;
                maximum = finiteCharges == 1 ? charge : Math.Max(maximum, charge);
                // Nonnegative incremental mean cannot overflow by summing huge Q.
                mean += (charge - mean) / finiteCharges;
            }
            summaryRows = rows.ToArray();
            summaryChargeCount = finiteCharges;
            summaryLocationCount=located;summaryMissingLocationCount=missingLocation;summaryOutOfRangeLocationCount=outOfRangeLocation;
            summaryMaximumCharge = maximum;
            summaryMeanCharge = finiteCharges == 0 ? double.NaN : mean;
            summaryDirty = false;
            if (summaryComputationCount < int.MaxValue) summaryComputationCount++;
        }

        string FormatSummaryCharge(double chargePc)
        {
            if (!Finite(chargePc)) return "—";
            double value = chargePc / ChargeDisplayScale;
            // Fixed-decimal output; a small measured value must not become a
            // misleading zero after conversion to nC or microcoulombs.
            string number = value > 0 && value < 0.1 ? "<0.1"
                : Math.Round(value, 1, MidpointRounding.AwayFromZero).ToString("0.#", CultureInfo.InvariantCulture);
            return number + " " + ChargeUnitLabel;
        }
    }
}
