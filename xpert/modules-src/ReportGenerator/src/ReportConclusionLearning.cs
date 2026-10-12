using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace HVX.ReportGenerator
{
    // Uses the analysis module's existing local transaction store, not a second database.
    internal static class ReportConclusionLearning
    {
        internal const string Kind = "report_conclusion_feedback";
        internal const string Schema = "report-conclusion-v1";
        static Type StoreType()
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "HVX.PdReview.V1.0");
            if (assembly == null)
            {
                string root = Path.GetDirectoryName(typeof(ReportConclusionLearning).Assembly.Location);
                string path = Path.GetFullPath(Path.Combine(root, "..", "DataAnalysis", "HVX.PdReview.V1.0.dll"));
                if (!File.Exists(path)) throw new InvalidOperationException("The local learning component is unavailable. Please repair the software installation.");
                assembly = Assembly.LoadFrom(path);
            }
            return assembly.GetType("HVX.PdReview.ReviewHybridLearningStore", true);
        }
        static object Invoke(string method, params object[] args)
        {
            MethodInfo entry = StoreType().GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic);
            if (entry == null) throw new InvalidOperationException("The local learning component requires an update.");
            try { return entry.Invoke(null, args); }
            catch (TargetInvocationException e) { throw e.InnerException ?? e; }
        }
        internal static string DefaultRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XIONGXIN", "HVXUnifiedDesktop", "LocalHybridAi"); } }
        internal static Dictionary<string, object>[] Read(string root)
        {
            return ((Dictionary<string, object>[])Invoke("Read", root, Kind))
                .Where(item => Text(item, "feature_schema") == Schema).ToArray();
        }
        internal static string Save(string root, string original, string current, string corrected, int rating,
            string notes, string language, Dictionary<string, object> context, double[] features, bool accepted)
        {
            if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(corrected)) throw new ArgumentException("The original and corrected conclusions are required.");
            if (original.Length > 32000 || (current ?? "").Length > 32000 || corrected.Length > 32000 || (notes ?? "").Length > 8000)
                throw new ArgumentException("The feedback text is too long.");
            if (rating < 1 || rating > 5) throw new ArgumentOutOfRangeException("rating");
            if (!ReportLanguages.Codes.Contains(language)) throw new ArgumentException("Unsupported feedback language.");
            if (context == null || features == null || features.Length != 8 || features.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                throw new ArgumentException("A valid data context is required.");
            object risk;if(context.TryGetValue("acquisition_reference_only",out risk)&&!Object.Equals(risk,false))throw new InvalidOperationException("Reference-only acquisition cannot be used as conclusion learning truth.");
            var values = new Dictionary<string, object> {
                { "feature_schema", Schema }, { "feature_vector", features }, { "language", language },
                { "original_conclusion", original }, { "report_conclusion_before_review", current ?? "" },
                { "corrected_conclusion", corrected }, { "rating", rating }, { "rating_scale", "1=poor; 5=excellent; rating describes the original conclusion" },
                { "notes", notes ?? "" }, { "data_context", context }, { "operator", Environment.UserName },
                { "human_acceptance_requested", accepted }, { "learning_method", "local_reference_retrieval_only" },
                { "production_model_modified", false }, { "control_build", typeof(ReportConclusionLearning).Assembly.GetName().Version.ToString() }
            };
            return Convert.ToString(Invoke("Add", root, Kind, values, null), CultureInfo.InvariantCulture);
        }
        internal static string Remove(string root, string id) { return Convert.ToString(Invoke("Delete", root, Kind, id), CultureInfo.InvariantCulture); }
        internal static Dictionary<string, object> Suggest(string root, string language, double[] features)
        {
            if (features == null || features.Length != 8) return null;
            return Read(root).Where(item => Text(item, "language") == language && Text(item, "corrected_conclusion").Length > 0)
                .Select(item => new { Item = item, Distance = Distance(features, item) })
                .Where(item => item.Distance <= 0.45).OrderBy(item => item.Distance)
                .ThenByDescending(item => Text(item.Item, "created_utc"), StringComparer.Ordinal)
                .Select(item => item.Item).FirstOrDefault();
        }
        static double Distance(double[] current, Dictionary<string, object> item)
        {
            object value; if (!item.TryGetValue("feature_vector", out value)) return double.PositiveInfinity;
            var list = value as IEnumerable; if (list == null) return double.PositiveInfinity;
            var stored = new List<double>();
            foreach (object entry in list) { double d; if (!double.TryParse(Convert.ToString(entry, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out d) || double.IsNaN(d) || double.IsInfinity(d)) return double.PositiveInfinity; stored.Add(d); }
            if (stored.Count != current.Length || stored[7] != current[7]) return double.PositiveInfinity;
            double distance = 0;
            for (int i = 0; i < current.Length - 1; i++) distance += Math.Abs(Math.Log(1 + Math.Abs(current[i])) - Math.Log(1 + Math.Abs(stored[i])));
            return distance / (current.Length - 1);
        }
        internal static string Text(Dictionary<string, object> item, string key)
        { object value; return item != null && item.TryGetValue(key, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; }
        internal static string MatrixHash(double[,] matrix)
        {
            using (var sha = SHA256.Create())
            using (var stream = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(matrix.GetLength(0)); writer.Write(matrix.GetLength(1));
                foreach (double value in matrix) writer.Write(value);
                writer.Flush(); stream.FlushFinalBlock();
                return BitConverter.ToString(sha.Hash).Replace("-", "");
            }
        }
    }
}
