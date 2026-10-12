using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HVX.ReportGenerator
{
    // Naming only: project and terminal names remain operator-authored text.
    // Leave room for the State Grid suffix and a collision sequence, including
    // on machines where Office still applies the traditional Windows path limit.
    internal static class ReportFileNames
    {
        static readonly string[] Kinds = {
            "局放检测报告", "Partial Discharge Test Report", "Доклад за изпитване на частични разряди",
            "Izvješće o ispitivanju parcijalnih izbijanja", "Zpráva o měření částečných výbojů",
            "Rapport de mesure des décharges partielles", "Teilentladungsprüfbericht",
            "Részleges kisülési vizsgálati jelentés", "Rapporto di prova delle scariche parziali",
            "Raport z pomiaru wyładowań niezupełnych", "Raport de testare a descărcărilor parțiale",
            "Отчёт об испытании частичных разрядов", "Izveštaj o ispitivanju parcijalnih pražnjenja",
            "Správa o meraní čiastočných výbojov", "Poročilo o meritvah delnih razelektritev",
            "Informe de ensayo de descargas parciales", "Kısmi Deşarj Test Raporu",
            "Báo cáo thử nghiệm phóng điện cục bộ", "局放檢測報告"
        };
        static readonly Regex DatedName = new Regex(@"^(?<stem>.+_(?<stamp>[0-9]{8}_[0-9]{6}))(?<tail>(?:_[0-9]{3,})?(?:_国网附录M|_國網附錄M|_StateGrid_AppendixM)?(?:_[0-9]{3,})?)$", RegexOptions.CultureInvariant);

        internal static string Kind(int language) { return Kinds[language >= 0 && language < Kinds.Length ? language : 1]; }
        internal static string GridSuffix(int language) { return language == 0 ? "_国网附录M" : language == 18 ? "_國網附錄M" : "_StateGrid_AppendixM"; }

        internal static string Stem(string directory, string project, string start, string end, int language, DateTime time)
        {
            directory = Path.GetFullPath(directory);
            int budget = Math.Min(200, 240 - directory.TrimEnd(Path.DirectorySeparatorChar).Length - 1 - 5 - 32);
            string[] original = { SafePart(project, "Project"), SafePart(start, "—"), SafePart(end, "—"), Kind(language) };
            int[] lengths = original.Select(x => x.Length).ToArray();
            const int minimum = 12;
            // yyyyMMdd_HHmmss plus four separators.
            while (lengths.Sum() + 19 > budget)
            {
                int longest = -1;
                for (int i = 0; i < lengths.Length; i++)
                    if (lengths[i] > minimum && (longest < 0 || lengths[i] > lengths[longest])) longest = i;
                if (longest < 0) throw new PathTooLongException("报告输出目录过长，请选择更短的目录。 / Choose a shorter report output folder.");
                lengths[longest]--;
            }
            string[] part = original.Select((x, i) => Shorten(x, lengths[i])).ToArray();
            return part[0] + "_" + part[1] + "—" + part[2] + "_" + part[3] + "_" + time.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        }

        internal static string AvailableBase(string directory, string project, string start, string end, int language, DateTime time, bool includeGrid)
        {
            string stem = Stem(directory, project, start, end, language, time);
            for (int number = 0; number < 1000000; number++)
            {
                string candidate = Path.Combine(directory, stem + (number == 0 ? "" : "_" + number.ToString("000", CultureInfo.InvariantCulture)));
                if (!PairExists(candidate) && (!includeGrid || !PairExists(candidate + GridSuffix(language)))) return candidate;
            }
            throw new IOException("无法选择不重名的报告文件。 / No unused report file name is available.");
        }

        internal static string AvailableGridBase(string normalBase, int language)
        {
            string stem = normalBase + GridSuffix(language);
            for (int number = 0; number < 1000000; number++)
            {
                string candidate = stem + (number == 0 ? "" : "_" + number.ToString("000", CultureInfo.InvariantCulture));
                if (!PairExists(candidate)) return candidate;
            }
            throw new IOException("No unused State Grid report file name is available.");
        }

        internal static string FindLatest(string directory, string project, string start, string end, int language)
        {
            if (!Directory.Exists(directory)) return "";
            string legacy = LegacySafe(project) + "_OWTS_Report_";
            var candidates = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(IsDocument).Select(path => new { Path = path, Language = NameLanguage(path, directory, project, start, end, legacy), Grid = IsGrid(path) })
                .Where(x => x.Language >= 0).ToList();
            // Prefer ordinary reports; a State Grid-only success remains openable.
            if (candidates.Any(x => !x.Grid)) candidates = candidates.Where(x => !x.Grid).ToList();
            if (candidates.Any(x => x.Language == language)) candidates = candidates.Where(x => x.Language == language).ToList();
            string latest = candidates.OrderByDescending(x => File.GetLastWriteTimeUtc(x.Path)).Select(x => x.Path).FirstOrDefault();
            if (string.IsNullOrEmpty(latest)) return "";
            string pdf = Path.ChangeExtension(latest, ".pdf");
            return File.Exists(pdf) && File.GetLastWriteTimeUtc(pdf) >= File.GetLastWriteTimeUtc(latest) ? pdf : latest;
        }

        static int NameLanguage(string path, string directory, string project, string start, string end, string legacy)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            for (int language = 0; language < ReportLanguages.Codes.Length; language++)
            {
                string oldBase = legacy + ReportLanguages.Codes[language];
                if (string.Equals(name, oldBase, StringComparison.OrdinalIgnoreCase) ||
                    Regex.IsMatch(name, "^" + Regex.Escape(oldBase) + @"_国网附录M_[0-9]{8}_[0-9]{6}(?:_[0-9a-f]{8})?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return language;
            }
            Match match = DatedName.Match(name);
            DateTime time;
            if (!match.Success || !DateTime.TryParseExact(match.Groups["stamp"].Value, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out time)) return -1;
            for (int language = 0; language < Kinds.Length; language++)
                if (string.Equals(match.Groups["stem"].Value, Stem(directory, project, start, end, language, time), StringComparison.OrdinalIgnoreCase)) return language;
            return -1;
        }

        static bool IsDocument(string path) { string ext = Path.GetExtension(path); return ext.Equals(".docx", StringComparison.OrdinalIgnoreCase) || ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase); }
        static bool IsGrid(string path) { Match match = DatedName.Match(Path.GetFileNameWithoutExtension(path)); string tail = match.Groups["tail"].Value; return tail.Contains("_国网附录M") || tail.Contains("_國網附錄M") || tail.Contains("_StateGrid_AppendixM"); }
        static bool PairExists(string path) { return File.Exists(path + ".docx") || File.Exists(path + ".pdf") || Directory.Exists(path + ".docx") || Directory.Exists(path + ".pdf"); }
        static string LegacySafe(string value) { string text = value ?? "OWTS"; foreach (char invalid in Path.GetInvalidFileNameChars()) text = text.Replace(invalid, '_'); return string.IsNullOrWhiteSpace(text) ? "OWTS" : text.Trim(); }
        static string SafePart(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            string result = new string(value.Select(c => char.IsControl(c) || Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
            if (result.Length == 0) result = fallback;
            // Changed/trimmed names retain a stable identity instead of colliding
            // with an operator name that already contains underscores.
            return result == value ? result : result + "~" + Hash(value);
        }
        static string Shorten(string value, int length)
        {
            if (value.Length <= length) return value;
            int prefix = length - 9;
            if (prefix > 0 && char.IsHighSurrogate(value[prefix - 1])) prefix--;
            return value.Substring(0, Math.Max(0, prefix)) + "~" + Hash(value);
        }
        static string Hash(string value) { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").Substring(0, 8); }
    }
}
