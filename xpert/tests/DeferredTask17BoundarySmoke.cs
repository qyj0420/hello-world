using System;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;

internal static class DeferredTask17BoundarySmoke
{
    static int checks;
    static void Check(bool ok, string text) { checks++; if (!ok) throw new InvalidOperationException(text); }
    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1) throw new ArgumentException("Pass the actual release build directory.");
            string root = Path.GetFullPath(args[0]);
            var manifest = new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, object>>(
                File.ReadAllText(Path.Combine(root, "build-manifest.json")));
            var deferred = (System.Collections.Generic.Dictionary<string, object>)manifest["Task17NeuralLearning"];
            Check(deferred["Included"] is bool && !(bool)deferred["Included"], "Task41 release explicitly excludes deferred Task17 neural integration");
            Check((string)deferred["Status"] == "DeferredAwaitingTask17FullCompletion", "Manifest explains why the neural candidate is not released");
            Check(deferred["BundledTrainedWeights"] is bool && !(bool)deferred["BundledTrainedWeights"], "No claim of bundled field neural weights");
            var addonSources = deferred["AddonSources"] as System.Collections.IList;
            Check(addonSources != null && addonSources.Count == 0, "Deferred learning source additions must be an empty JSON array, not active release inputs");
            string moduleRoot = Path.Combine(root, "modules", "DataAnalysis");
            AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) => {
                string dependency = Path.Combine(moduleRoot, new AssemblyName(eventArgs.Name).Name + ".dll");
                return File.Exists(dependency) ? Assembly.LoadFrom(dependency) : null;
            };
            Assembly algorithm = Assembly.LoadFrom(Path.Combine(moduleRoot, "fc40b1d6ae7648e9b4abf7b9.V1.0.17.dll"));
            Check(algorithm.GetType("HVX.Task17.Location.ReflectionLearningEngine", false) == null, "Actual shipped physical algorithm contains no deferred neural engine");
            Assembly control = Assembly.LoadFrom(Path.Combine(moduleRoot, "HVX.PdReview.V1.0.dll"));
            Check(control.GetType("HVX.PdReview.ReviewReflectionLearningRepository", false) == null, "Actual shipped GUI contains no deferred neural repository");
            Type review = control.GetType("HVX.PdReview.PdReviewControl", true);
            foreach (string method in new[] { "RebuildReflectionLearningModelAsync", "RequestReflectionLearningSuggestionAsync", "ApplyReflectionLearningSuggestionAsync", "RecordCurrentReflectionRejectionAsync" })
                Check(review.GetMethod(method, BindingFlags.Public | BindingFlags.Instance) == null, "No deferred neural GUI action: " + method);
            foreach (string method in new[] { "GetLocalAiAnalysisSuggestion", "GetLocalAiConclusionSuggestion", "ApplyLocalAiAnalysisSuggestion" })
                Check(review.GetMethod(method, BindingFlags.Public | BindingFlags.Instance) != null, "Existing non-neural local review API remains: " + method);
            Console.WriteLine("DEFERRED_TASK17_BOUNDARY_PASS=" + checks + "; HARDWARE_COMMANDS=0; NEURAL_CANDIDATE_INCLUDED=False");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
