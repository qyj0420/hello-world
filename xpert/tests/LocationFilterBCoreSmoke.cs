using System;
using System.Diagnostics;
using HVX.PD.LocationFiltering;

// Offline only. Compile with both Task28 core source files; do not deploy this executable.
internal static class LocationFilterBCoreSmoke
{
    private static int checks;
    private static void Check(bool condition, string message)
    { checks++; if (!condition) throw new Exception(message); }
    private static void Throws(Action action, string message)
    {
        bool thrown = false;
        try { action(); } catch (ArgumentException) { thrown = true; }
        Check(thrown, message);
    }
    private static double[,] Rows(params double[][] events)
    {
        double[,] matrix = new double[events.Length, 36];
        for (int i = 0; i < events.Length; i++)
        {
            matrix[i, 0] = events[i][0]; matrix[i, 5] = events[i][1]; matrix[i, 13] = events[i][2];
            matrix[i, 15] = events[i][3]; matrix[i, 2] = events[i][4]; matrix[i, 35] = 1;
            matrix[i, 1] = 7; // Same LOC deliberately: each event must still vote.
        }
        return matrix;
    }
    private static double[] E(double phase, double length, double x, double q, double v)
    { return new double[] { phase, length, x, q, v }; }
    private static LocationFilterBResult Run(double[,] matrix, LocationFilterBOptions options)
    { bool[] complete = new bool[matrix.GetLength(0)]; for (int i = 0; i < complete.Length; i++) complete[i] = true; return LocationClusterStrengthFilterB.Evaluate(matrix, complete, options); }
    private static void Expect(LocationFilterBResult result, string keep, string unknown, string message)
    {
        Check(result.KeepMask.Length == keep.Length, message + " length");
        int retained = 0, uncertain = 0;
        for (int i = 0; i < keep.Length; i++)
        {
            Check(result.KeepMask[i] == (keep[i] == '1'), message + " keep " + i);
            Check(result.UnknownMask[i] == (unknown[i] == '1'), message + " unknown " + i);
            if (result.KeepMask[i]) retained++; if (result.UnknownMask[i]) uncertain++;
        }
        Check(result.RetainedRows == retained && result.UnknownRows == uncertain, message + " totals");
    }
    public static int Main()
    {
        try
        {
            LocationFilterBOptions o = new LocationFilterBOptions();
            Check(o.Strength == 40 && o.ChargeThresholdPc == 20 && o.MinimumClusterSize == 3 && o.ToM == null && o.VoltageLimitKv == double.MaxValue, "defaults");
            LocationFilterBOptions cloned = o.Clone(); cloned.Strength = 6; Check(o.Strength == 40, "clone independent");
            // L1000 => w10, h5. Three same-LOC events in [495,505] meet minimum3;
            // mean=.003 and relative threshold=.12, so low-Q neighbors are retained.
            double[,] m = Rows(E(1,1000,500,21,10), E(1,1000,501,1,10), E(1,1000,502,20,10), E(2,1000,501,1,10), E(0,1000,501,1,10), E(1,999,501,1,10));
            double[,] original = (double[,])m.Clone();
            Expect(Run(m,o), "111000", "000000", "hand count, lowQ, phase and exact length separation");
            for (int r = 0; r < m.GetLength(0); r++) for (int c = 0; c < 36; c++) Check(m[r,c] == original[r,c], "unchanged input");
            Expect(Run(Rows(E(1,1000,500,20,10)),o), "0", "0", "strict seed threshold");
            Expect(Run(Rows(E(1,1000,500,20.0000001,10)),o), "0", "0", "f32 quantization before threshold");
            foreach (double strength in new double[] {0,5,6})
            {
                o.Strength = strength;
                Expect(Run(Rows(E(1,1000,500,-21,10),E(1,1000,700,1,10)),o), strength <= 5 ? "10" : "00", "00", "strength switch " + strength);
            }
            o.Strength = 5;
            Expect(Run(Rows(E(1,100,50,21,10),E(1,100,49,1,10),E(1,100,51,1,10),E(1,100,52,1,10)),o), "1110", "0000", "closed seed endpoints");
            // 49.5 -> 50 (even), so bin49 is outside; lookup 50.5 -> 50.
            Expect(Run(Rows(E(1,100,50.5,21,10),E(1,100,49,1,10),E(1,100,52,1,10)),o), "101", "000", "nearest even seed edges");
            o.Strength = 40;
            // L200 => w2,h1; j0 sees [1,0,1], two events produce count3 by left mirroring.
            Expect(Run(Rows(E(1,200,0,21,10),E(1,200,1,1,10)),o), "10", "00", "left mirror duplicates bins");
            Expect(Run(Rows(E(1,200,199,21,10),E(1,200,200,1,10)),o), "11", "10", "unsafe right unknown and terminal exempt");
            Expect(Run(Rows(E(1,200,199,1,10),E(1,200,200,1,10)),o), "00", "00", "unseeded right edge never unknown");
            o.VoltageLimitKv = 10;
            Expect(Run(Rows(E(1,200,199,21,11),E(1,200,200,1,10)),o), "01", "00", "above voltage still seeds but is hidden");
            o.Strength = 5;
            Expect(Run(Rows(E(1,1000,500,21,10.04),E(1,1000,501,1,10.06)),o), "10", "00", "rounded voltage cutoff");
            o.VoltageLimitKv = double.MaxValue; o.Strength = 40;
            Expect(Run(Rows(E(1,200,199,21,10),E(1,200,200,1,10)),new LocationFilterBOptions {ToM=199}), "00", "00", "range removes unknown and endpoint");
            o.Strength = 5; o.FromM = 10; o.ToM = 12;
            Expect(Run(Rows(E(1,100,10,21,10),E(1,100,11,21,10),E(1,100,12,21,10)),o), "010", "000", "strict From To boundaries");
            o.FromM = 0; o.ToM = null;
            Expect(Run(Rows(E(1,100,0,21,10),E(1,100,100,21,10)),o), "11", "00", "default range preserves endpoints");
            // L100 => w1,h0. End bin must not add density votes to bin99.
            o.Strength = 6;
            Expect(Run(Rows(E(1,100,99,21,10),E(1,100,100,21,10),E(1,100,100,21,10)),o), "011", "000", "terminal not counted and density exempt");
            // L1193, w12/h6 gives13 actual bins; all 3 events fit exactly at +/-6.
            Expect(Run(Rows(E(1,1193,500,21,10),E(1,1193,494,1,10),E(1,1193,506,1,10)),o), "100", "000", "actual width13 not12");
            o = new LocationFilterBOptions { Strength = 100, MinimumClusterSize = 0 };
            // L10/w1/h0: 2 events in distinct bins; relative threshold2 excludes singletons.
            Expect(Run(Rows(E(1,10,4,21,10),E(1,10,6,21,10)),o), "00", "00", "relative density rejects");
            m = Rows(E(1,100,50,21,10),E(1,100,-1,21,10),E(1.5,100,50,21,10),E(1,0,0,21,10),E(1,100,50,double.NaN,10),E(1,100,50,21,0),E(1,100,50,21,-1),E(1,100,50,double.MaxValue,10),E(1,100,50,21,double.PositiveInfinity));
            o = new LocationFilterBOptions { Strength = 0 };
            Expect(Run(m,o), "100000000", "000000000", "invalid input exclusions");
            Check(Run(m,o).EligibleRows == 1 && Run(m,o).DiagnosticsJson.Contains("\"invalidRows\":7"), "invalid diagnostics");
            LocationFilterBResult underflow = Run(Rows(E(1,100,50,21,1e-50)),o);
            Expect(underflow, "0", "0", "host excludes voltage quantized to zero");
            Check(underflow.EligibleRows == 0 && underflow.DiagnosticsJson.Contains("\"unrepresentableRows\":1"), "positive voltage underflow diagnosed");
            m = Rows(E(1,100,50,21,10),E(1,100,50,21,10)); m[1,35] = 0;
            Expect(LocationClusterStrengthFilterB.Evaluate(m,new bool[]{false,true},o),"00","00","completeness and acceptance");
            Throws(delegate { Run(new double[1,35],o); }, "columns budget");
            Throws(delegate { Run(new double[200001,36],o); }, "row budget");
            Throws(delegate { Run(new double[1,10000001],o); }, "cell budget including extra cols");
            Throws(delegate { LocationClusterStrengthFilterB.Evaluate(new double[1,36],null,o); }, "completeness required");
            Throws(delegate { LocationClusterStrengthFilterB.ValidateOptions(new LocationFilterBOptions {Strength=101}); }, "strength host range");
            Throws(delegate { LocationClusterStrengthFilterB.ValidateOptions(new LocationFilterBOptions {Strength=double.NaN}); }, "NaN strength");
            Throws(delegate { LocationClusterStrengthFilterB.ValidateOptions(new LocationFilterBOptions {ChargeThresholdPc=-1}); }, "negative threshold");
            Throws(delegate { LocationClusterStrengthFilterB.ValidateOptions(new LocationFilterBOptions {VoltageLimitKv=double.PositiveInfinity}); }, "infinite voltage limit");
            Throws(delegate { LocationClusterStrengthFilterB.ValidateOptions(new LocationFilterBOptions {MinimumClusterSize=-1}); }, "minimum negative");
            Throws(delegate { LocationClusterStrengthFilterB.ValidateOptions(new LocationFilterBOptions {FromM=20,ToM=10}); }, "reversed range");
            Throws(delegate { Run(Rows(E(1,10,5,21,10)),new LocationFilterBOptions {ToM=11}); }, "no silent range clipping");
            double[,] manyGroups = new double[LocationClusterStrengthFilterB.MaximumGroups+1,36];
            for(int i=0;i<manyGroups.GetLength(0);i++) {manyGroups[i,0]=1;manyGroups[i,2]=10;manyGroups[i,5]=90000+i/100.0;manyGroups[i,13]=500;manyGroups[i,15]=21;manyGroups[i,35]=1;}
            Throws(delegate { Run(manyGroups,o); }, "exact geometry group budget");
            // Repeated high-Q seeds on a 100km cable must not perform 200000*2001 writes.
            double[,] large = new double[200000,36];
            for(int i=0;i<large.GetLength(0);i++) {large[i,0]=1;large[i,2]=10;large[i,5]=100000;large[i,13]=50000;large[i,15]=21;large[i,35]=1;}
            Stopwatch watch = Stopwatch.StartNew();
            LocationFilterBResult stress = Run(large,new LocationFilterBOptions()); watch.Stop();
            Check(stress.RetainedRows == 200000 && stress.UnknownRows == 0,"large sparse group preserves votes");
            Console.WriteLine("PASS " + checks + " checks; 200000-row sparse case " + watch.ElapsedMilliseconds + "ms");
            return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
