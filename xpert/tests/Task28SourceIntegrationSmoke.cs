// Offline deployment test: reflects the real DataAnalysis assembly, never a hardware host.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows.Forms;

static class Task28SourceIntegrationSmoke
{
    static int checks;
    static Type engineType;
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Check(bool ok, string text) { if (!ok) throw new Exception(text); checks++; }
    static object Invoke(object target, string name, params object[] args)
    {
        try { return target.GetType().GetMethod(name, Private | BindingFlags.Public).Invoke(target, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
    }
    static void Field(object target, string name, object value) { target.GetType().GetField(name, Private).SetValue(target, value); }
    static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, Private).GetValue(target); }
    static T Property<T>(object target, string name) { return (T)target.GetType().GetProperty(name).GetValue(target, null); }
    sealed class Result { public double[,] Kept, Gated, Scores, Clusters; public int[] Indices; public string Json; }
    static Result Filter(double[,] matrix, double strength = 0, double radius = 4, bool compensated = true, bool ends = false)
    {
        var args = new object[] { matrix, strength, radius, 3, ends, compensated, null, null, null, null, null, null };
        Invoke(Activator.CreateInstance(engineType), "FilterLocationMatrixAdvanced", args);
        return new Result { Kept = (double[,])args[6], Gated = (double[,])args[7], Indices = (int[])args[8],
            Scores = (double[,])args[9], Clusters = (double[,])args[10], Json = (string)args[11] };
    }
    static double[,] Rows(int count, int columns = 39)
    {
        var result = new double[count, columns];
        for (int r = 0; r < count; r++)
        {
            result[r, 0] = 0; result[r, 1] = r; result[r, 2] = 20; result[r, 5] = 1000;
            result[r, 6] = 160; result[r, 7] = 100000000; result[r, 10] = 100 + r * 3;
            result[r, 13] = 500; result[r, 15] = 100; result[r, 16] = 120; result[r, 22] = .95;
            result[r, 28] = 5; result[r, 33] = .9; result[r, 35] = 1;
            if (columns > 36) { result[r, 36] = 100000 + r; result[r, 37] = double.NaN; result[r, 38] = -r; }
        }
        return result;
    }
    static void Same(double[,] left, double[,] right, string label)
    {
        Check(left.GetLength(0) == right.GetLength(0) && left.GetLength(1) == right.GetLength(1), label + " dimensions");
        for (int r = 0; r < left.GetLength(0); r++) for (int c = 0; c < left.GetLength(1); c++)
            Check(left[r, c].Equals(right[r, c]), label + " [" + r + "," + c + "]");
    }
    static void Core()
    {
        var matrix = Rows(8); var before = (double[,])matrix.Clone(); var output = Filter(matrix);
        var json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(output.Json);
        Check((string)json["version"] == "1.2.1.0", "deployed Task28 engine version 1.2.1.0");
        Same(matrix, before, "source is immutable");
        Check(output.Kept.GetLength(1) == 39 && output.Gated.GetLength(1) == 39, "extension columns preserved");
        for (int i = 0; i < output.Indices.Length; i++) for (int c = 0; c < 39; c++)
            Check(output.Kept[i, c].Equals(matrix[output.Indices[i], c]), "retained original row including NaN extension");

        var normal = Rows(8); var huge = Rows(8);
        for (int r = 0; r < 8; r++) { normal[r, 6] = 1; normal[r, 7] = 10; huge[r, 6] = 1e307; huge[r, 7] = 1e308; }
        Check(Filter(normal, 0, -1).Clusters[0, 3] == 50 && Filter(huge, 0, -1).Clusters[0, 3] == 50,
            "finite extreme equivalent v/fs keeps same 50m resolution cap");
        var tiny = Rows(4);
        for (int r = 0; r < 4; r++) tiny[r, 15] = tiny[r, 16] = tiny[r, 28] = double.Epsilon;
        Check(Filter(tiny).Clusters[0, 9] == double.Epsilon, "positive subnormal median remains positive");

        var duplicates = Rows(12);
        for (int r = 0; r < 12; r++)
        { duplicates[r, 1] = r / 2; duplicates[r, 10] = 100; duplicates[r, 15] = 80;
          duplicates[r, 16] = r % 2 == 0 ? 100 : 1000; duplicates[r, 28] = r % 2 == 0 ? 2 : 4; }
        Check(Filter(duplicates).Clusters[0, 10] == Filter(duplicates, 0, 4, false).Clusters[0, 10],
            "display compensation cannot change raw-SNR representative");
        var translated = Rows(36);
        for (int r = 0; r < 36; r++) { translated[r, 1] = r / 3; translated[r, 13] = r % 3 == 0 ? 103.9 : r % 3 == 1 ? 104.1 : 300; }
        var shifted = (double[,])translated.Clone(); for (int r = 0; r < 36; r++) shifted[r, 13] += .2;
        Check(Math.Abs(Filter(translated).Clusters[0, 7] - Filter(shifted).Clusters[0, 7]) < 1e-9,
            "rigid .2m translation keeps evidence score");
        Check(Filter(translated, 79.8).Indices.Length == Filter(shifted, 79.8).Indices.Length, "translation keeps threshold result");
        bool rejected = false;
        try { Filter((double[,])Array.CreateInstance(typeof(double), new[] { 1, 36 }, new[] { 1, 0 })); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "nonzero lower bound explicitly rejected");

        matrix = Rows(8); matrix[0, 35] = 0; matrix[1, 2] = 0; matrix[2, 13] = 0; matrix[3, 13] = 1000;
        output = Filter(matrix, 100, 4, true, true);
        Check(!output.Indices.Contains(0) && !output.Indices.Contains(1), "upstream rejected and zero voltage never revived");
        Check(output.Indices.Contains(2) && output.Indices.Contains(3) && output.Scores[2, 5] == 0,
            "protected ends retained without evidence confidence");
        var sparse = Rows(20); for (int r = 0; r < 20; r++) sparse[r, 1] = r % 2;
        output = Filter(sparse, 40);
        Check(output.Indices.Length == 0 && output.Scores[0, 5] <= 39.9,
            "retained limitation: two independent LOCs remain hidden by default40; not represented as fixed");
    }
    static void Host(Assembly assembly)
    {
        using (var control = (Control)Activator.CreateInstance(assembly.GetType("HVX.PdReview.PdReviewControl", true)))
        {
            var source = Rows(14); source[12, 13] = 0; source[13, 13] = 1000;
            var before = (double[,])source.Clone();
            Field(control, "working", source); Field(control, "locationFilterEnabled", true);
            Field(control, "locationFilterStrength", 0d); Field(control, "capacitorProject", false);
            Invoke(control, "InvalidateLocationScores");
            var all = Enumerable.Repeat(true, 14).ToArray(); var mask = (bool[])all.Clone();
            Invoke(control, "ApplyLocationFilter", mask, all);
            var status = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Property<string>(control, "LocationFilteringDiagnosticsJson"));
            Check((string)((Dictionary<string, object>)status["scoreBuildAtStrengthZero"])["version"] == "1.2.1.0",
                "real control consumes new engine (outer legacy source label is not version)");
            Check(Property<int>(control, "LocationScoreBuildCount") == 1, "first score builds once");
            var first = (double[,])Field<double[,]>(control, "locationScores").Clone();
            foreach (double strength in new[] { 0d, 19.9, 39.9, 40, 80, 100, 0 })
            {
                Field(control, "locationFilterStrength", strength); mask = (bool[])all.Clone(); mask[0] = false;
                Invoke(control, "ApplyLocationFilter", mask, all);
                Check(!mask[0], "preexisting mask never revived at " + strength);
                Check(Property<int>(control, "LocationScoreBuildCount") == 1, "strength change uses score cache " + strength);
            }
            Same(first, Field<double[,]>(control, "locationScores"), "score cache stable");
            for (int r = 0; r < 14; r++) { source[r, 22] = r % 2 == 0 ? 1e100 : double.NaN; source[r, 33] = double.PositiveInfinity; }
            Invoke(control, "InvalidateLocationScores"); mask = (bool[])all.Clone(); Invoke(control, "ApplyLocationFilter", mask, all);
            Same(first, Field<double[,]>(control, "locationScores"), "Task17 col22/33 not used as standalone discharge evidence");
            var complete = (bool[])all.Clone(); complete[1] = false;
            mask = (bool[])all.Clone(); Invoke(control, "ApplyLocationFilter", mask, complete);
            Check(!mask[1] && Property<int>(control, "LocationScoreBuildCount") == 3, "completeness changes invalidate and exclude");
            Field(control, "capacitorProject", true); mask = (bool[])all.Clone(); mask[0] = false;
            Invoke(control, "ApplyLocationFilter", mask, all);
            Check(mask.Skip(1).All(x => x) && !mask[0], "capacitor bypass leaves upstream mask untouched");
            for (int r = 0; r < 14; r++) { before[r, 22] = source[r, 22]; before[r, 33] = source[r, 33]; }
            Same(source, before, "real host scoring never overwrites source acceptance or extension columns");
        }
    }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1) throw new ArgumentException("Usage: Task28SourceIntegrationSmoke BuildRoot");
            string path = Path.Combine(Path.GetFullPath(args[0]), "modules", "DataAnalysis", "HVX.PdReview.V1.0.dll");
            Application.EnableVisualStyles(); var assembly = Assembly.LoadFrom(path);
            engineType = assembly.GetType("HVX.PD.LocationFiltering.LocationClusterFilterEngine", true);
            Core(); Host(assembly);
            Console.WriteLine("PASS " + checks + " TASK28_SOURCE_INTEGRATION; HARDWARE_COMMANDS=0; PROJECT_WRITES=0"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
