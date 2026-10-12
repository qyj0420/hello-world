using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace HVX.Controls
{
    // A reusable voltage plan contains only multipliers and repetitions. It is
    // not a device configuration or an authorization to energize the equipment.
    internal sealed class VoltagePlan
    {
        internal string Name;
        internal List<PressureStep> Steps;
    }

    internal static class VoltagePlanFile
    {
        internal const string Extension = ".hvplan";
        internal const int MaxRows = 512;
        internal const int MaxBytes = 262144;
        internal const string InvalidMessage = "加压计划无效：需包含 1–512 行；倍数应为 0–100，次数应为 1–10000 的整数，总次数不超过 1000000。";

        internal static string DefaultDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "XIONGXIN", "HV DAC Xpert", "Voltage Plans"); }
        }

        internal static void Validate(IEnumerable<PressureStep> source)
        {
            if (source == null) throw new InvalidDataException(InvalidMessage);
            int rows = 0;
            long repetitions = 0;
            foreach (PressureStep step in source)
            {
                if (++rows > MaxRows || step == null || double.IsNaN(step.Multiplier) || double.IsInfinity(step.Multiplier) ||
                    step.Multiplier < 0 || step.Multiplier > 100 || step.Repetitions < 1 || step.Repetitions > 10000)
                    throw new InvalidDataException(InvalidMessage);
                repetitions += step.Repetitions;
                if (repetitions > 1000000) throw new InvalidDataException(InvalidMessage);
            }
            if (rows == 0) throw new InvalidDataException(InvalidMessage);
        }

        static string ValidateName(string name)
        {
            string clean = (name ?? string.Empty).Trim();
            if (clean.Length == 0 || clean.Length > 100 || clean.Any(char.IsControl))
                throw new InvalidDataException("计划名称应为 1–100 个字符，不能包含控制字符。");
            return clean;
        }

        internal static string SuggestedFileName(string name)
        {
            string clean = new string((name ?? string.Empty).Where(c => !char.IsControl(c) && !Path.GetInvalidFileNameChars().Contains(c)).Take(90).ToArray()).Trim(' ', '.');
            return (clean.Length == 0 ? "Voltage Plan" : clean) + Extension;
        }

        internal static void Save(string path, string name, IEnumerable<PressureStep> source)
        {
            string full = ValidatePath(path);
            string cleanName = ValidateName(name);
            List<PressureStep> steps = source == null ? null : source.Take(MaxRows + 1).Select(s => s == null ? null : s.Clone()).ToList();
            Validate(steps);
            var document = new Dictionary<string, object>
            {
                { "format", "XIONGXIN.VoltagePlan" }, { "version", 1 }, { "name", cleanName },
                { "steps", steps.Select(s => new Dictionary<string, object> { { "multiplier", s.Multiplier }, { "repetitions", s.Repetitions } }).ToArray() }
            };
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(new JavaScriptSerializer { MaxJsonLength = MaxBytes, RecursionLimit = 8 }.Serialize(document));
            if (bytes.Length > MaxBytes) throw new InvalidDataException("加压计划文件过大。");
            string directory = Path.GetDirectoryName(full);
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                // An existing file is replaced atomically only after the entire
                // new file has been validated, written, and flushed.
                if (File.Exists(full)) File.Replace(temporary, full, null);
                else File.Move(temporary, full);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static VoltagePlan Load(string path)
        {
            string full = ValidatePath(path);
            string text;
            using (FileStream stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length <= 0 || stream.Length > MaxBytes) throw new InvalidDataException("加压计划文件大小无效。");
                using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false, true), true)) text = reader.ReadToEnd();
            }
            Dictionary<string, object> document;
            try { document = new JavaScriptSerializer { MaxJsonLength = MaxBytes, RecursionLimit = 8 }.DeserializeObject(text) as Dictionary<string, object>; }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException)
            { throw new InvalidDataException("无法读取加压计划文件，请选择有效的 .hvplan 文件。", error); }
            if (document == null || document.Count != 4 || !document.ContainsKey("format") || !document.ContainsKey("version") ||
                !document.ContainsKey("name") || !document.ContainsKey("steps") ||
                !string.Equals(document["format"] as string, "XIONGXIN.VoltagePlan", StringComparison.Ordinal) ||
                !(document["version"] is int) || (int)document["version"] != 1)
                throw new InvalidDataException("不支持此加压计划格式或版本。");
            string name = ValidateName(document["name"] as string);
            object[] entries = document["steps"] as object[];
            if (entries == null || entries.Length == 0 || entries.Length > MaxRows) throw new InvalidDataException(InvalidMessage);
            List<PressureStep> steps = new List<PressureStep>();
            foreach (object value in entries)
            {
                var row = value as Dictionary<string, object>;
                if (row == null || row.Count != 2 || !row.ContainsKey("multiplier") || !row.ContainsKey("repetitions") ||
                    !(row["repetitions"] is int) || !IsNumeric(row["multiplier"])) throw new InvalidDataException(InvalidMessage);
                steps.Add(new PressureStep { Multiplier = Convert.ToDouble(row["multiplier"], CultureInfo.InvariantCulture), Repetitions = (int)row["repetitions"] });
            }
            Validate(steps);
            return new VoltagePlan { Name = name, Steps = steps };
        }

        static bool IsNumeric(object value) { return value is int || value is long || value is decimal || value is double; }

        static string ValidatePath(string path)
        {
            string full = Path.GetFullPath(path);
            if (!string.Equals(Path.GetExtension(full), Extension, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("请选择 .hvplan 加压计划文件。");
            return full;
        }
    }
}
