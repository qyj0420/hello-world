using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Xiongxin.Owts.Contracts.Ai;
using Xiongxin.Owts.Contracts.Algorithms;
using Xiongxin.Owts.Contracts.Persistence;

namespace Xiongxin.Owts.HostAdapters
{
    /// <summary>
    /// 把一次滤波/定位/AI/结论结果存为 SQLite analysis 快照。报告只读这里，不现场重算。
    /// ReportInputContext：每行 "key\tvalue"；SessionJson：同内容的扁平 JSON。
    /// </summary>
    public static class AnalysisSnapshotWriter
    {
        public const string AlgorithmIdSimplified = "SimplifiedPeakScan/v1";

        public static Guid Save(IProjectRepository repository, string filterId, LocationAnalysisResult location,
            DischargeRecognitionResultDto ai, ConclusionResult conclusion, bool liveHardware)
        {
            return Save(repository, filterId, location, ai, conclusion, liveHardware, Guid.Empty);
        }

        public static Guid Save(IProjectRepository repository, string filterId, LocationAnalysisResult location,
            DischargeRecognitionResultDto ai, ConclusionResult conclusion, bool liveHardware, Guid acquisitionId)
        {
            if (repository == null) throw new ArgumentNullException("repository");
            var kv = new List<KeyValuePair<string, string>>();
            Add(kv, "source", liveHardware ? "live" : "simulation");
            Add(kv, "filter.id", filterId);
            if (acquisitionId != Guid.Empty)
                Add(kv, "acquisition.id", acquisitionId.ToString("D"));
            if (location != null)
            {
                Add(kv, "location.success", location.Success ? "true" : "false");
                Add(kv, "location.status", location.Status);
                Add(kv, "location.rows", (location.Parameters != null ? location.Parameters.RowCount : 0).ToString(CultureInfo.InvariantCulture));
                Add(kv, "location.threshold", location.AppliedThreshold.ToString("G6", CultureInfo.InvariantCulture));
            }
            if (ai != null)
            {
                Add(kv, "ai.success", ai.Success ? "true" : "false");
                Add(kv, "ai.status", ai.StatusCode);
                Add(kv, "ai.message", ai.StatusMessage);
            }
            Add(kv, "conclusion.success", conclusion != null && conclusion.Success ? "true" : "false");
            Add(kv, "conclusion.message", conclusion != null ? conclusion.StatusMessage : "(none)");

            var ctx = new StringBuilder();
            var json = new StringBuilder("{");
            for (int i = 0; i < kv.Count; i++)
            {
                ctx.Append(kv[i].Key).Append('\t').Append(kv[i].Value).Append('\n');
                if (i > 0) json.Append(',');
                json.Append('"').Append(Escape(kv[i].Key)).Append("\":\"").Append(Escape(kv[i].Value)).Append('"');
            }
            json.Append('}');

            return repository.InsertAnalysis(new AnalysisPackageDto
            {
                AcquisitionId = acquisitionId,
                AlgorithmId = AlgorithmIdSimplified,
                SessionJson = json.ToString(),
                ReportInputContext = ctx.ToString(),
                CreatedUtc = DateTime.UtcNow
            });
        }

        public static IList<KeyValuePair<string, string>> ParseContext(string context)
        {
            var list = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(context)) return list;
            foreach (string line in context.Split('\n'))
            {
                if (line.Length == 0) continue;
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                list.Add(new KeyValuePair<string, string>(line.Substring(0, tab), line.Substring(tab + 1)));
            }
            return list;
        }

        static void Add(List<KeyValuePair<string, string>> kv, string key, string value)
        {
            kv.Add(new KeyValuePair<string, string>(key, (value ?? string.Empty).Replace('\t', ' ').Replace('\n', ' ').Replace("\r", "")));
        }

        static string Escape(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? string.Empty)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
