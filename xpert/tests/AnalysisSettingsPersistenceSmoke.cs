using System;
using System.IO;
using Xiongxin.OWTS.Desktop;

internal static class AnalysisSettingsPersistenceSmoke
{
    public sealed class FakeAnalysis
    {
        public double AnalysisAttenuationTolerancePercent { get; set; } = 22.5;
        public double AnalysisCorrelationErrorPercent { get; set; } = 18.25;
        public double AnalysisAutomaticThresholdMultiplier { get; set; } = 4.5;
        public int AnalysisMaximumPulseCount { get; set; } = 42;
        public int AnalysisMultipleReflectionSuppressionCount { get; set; } = 2;
        public int AnalysisAlgorithmIndex { get; set; } = 4;
        public bool AnalysisUseFirstArrivalTiming { get; set; } = true;
        public bool AnalysisPhaseFilterEnabled { get; set; } = true;
        public bool[] AnalysisQuadrants { get; set; } = new[] { true, false, false, true };
        public string ModelLibraryPath { get; set; } = "fixture.pdm";
    }
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static int Main(string[] args)
    {
        try
        {
            string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            var current = new AppSettings { SupportsCombinedVlf = true, DividerRatio = 17.5 };
            var source = new FakeAnalysis();
            AppSettings edited = AnalysisSettingsPersistence.Capture(source, current);
            Check(current.TaskMinus3.AttenuationTolerancePercent == 15, "Capture never mutates current settings before save");
            Check(edited.DividerRatio == 17.5 && edited.SupportsCombinedVlf, "Other device parameters preserved");
            string file = Path.Combine(output, "analysis-settings.xml"); edited.Save(file);
            var read = AppSettings.Load(file).TaskMinus3;
            Check(read.AttenuationTolerancePercent == 22.5 && read.CorrelationErrorPercent == 18.25 && read.AutomaticThresholdMultiplier == 4.5, "Threshold settings round trip");
            Check(read.MaximumPulseCount == current.TaskMinus3.MaximumPulseCount && read.MultipleReflectionSuppressionCount == current.TaskMinus3.MultipleReflectionSuppressionCount && read.AlgorithmIndex == current.TaskMinus3.AlgorithmIndex, "Ordinary dialog preserves Plus Data algorithm settings");
            Check(read.UseFirstArrivalTiming && read.PhaseFilterEnabled, "Boolean settings round trip");
            Check(read.Quadrant0To90 && !read.Quadrant90To180 && !read.Quadrant180To270 && read.Quadrant270To360, "Phase settings round trip");
            Check(read.ModelLibraryPath == current.TaskMinus3.ModelLibraryPath, "Ordinary dialog preserves Plus Data model setting");
            bool rejected = false;
            source.AnalysisQuadrants = new[] { true };
            try { AnalysisSettingsPersistence.Capture(source, current); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Malformed phase selection rejected");
            source.AnalysisQuadrants = new[] { true, true, true, true };
            source.AnalysisAttenuationTolerancePercent = double.NaN; rejected = false;
            try { AnalysisSettingsPersistence.Capture(source, current); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Nonfinite parameter rejected");
            rejected = false; try { AnalysisSettingsPersistence.Capture(new object(), current); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Missing public contract not guessed");
            Console.WriteLine("ANALYSIS_SETTINGS_PERSISTENCE_PASS=" + checks); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
