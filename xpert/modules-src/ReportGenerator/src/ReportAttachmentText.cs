using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace HVX.ReportGenerator
{
    /// <summary>Localizes machine-generated archive labels without changing project-entered fields.</summary>
    internal static class ReportAttachmentText
    {
        static readonly Regex Han = new Regex(@"[\u3400-\u9fff]", RegexOptions.Compiled);
        static readonly Regex Region = new Regex(
            @"^(?<id>R\d+)\s*·\s*(?<source>[A-Za-z0-9-]+)\s*选区边界:\s*L\s*(?<l>[^;]+);\s*Q\s*(?<q>[^;]+);\s*(?<rect>\d+)\s*矩形;\s*当前数据:\s*(?<count>\d+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex PhasePrefix = new Regex(@"(?<![A-Za-z0-9])(?<phase>[ABC])(?=_|相|\b)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal static bool ContainsHan(string value)
        {
            return !string.IsNullOrEmpty(value) && Han.IsMatch(value);
        }

        internal static string Title(ReportImageInfo image, int language)
        {
            if (image == null) return string.Empty;
            string source = string.IsNullOrWhiteSpace(image.Title) ? image.ChartKind : image.Title;
            if (language == 0) return source;
            string value = PhasePrefix.Replace(source, match =>
                "L" + (match.Groups["phase"].Value[0] - 'A' + 1).ToString(CultureInfo.InvariantCulture));
            value = value.Replace("相", string.Empty);
            value = value.Replace("区域统计", ReportLanguages.Text(language, ReportLanguages.UiKeys.RegionalStatisticsChart));
            value = value.Replace("反射波未确定", ReportLanguages.Text(language, ReportLanguages.UiKeys.ReflectionUncertain));
            if (!ContainsHan(value)) return value;
            string marker = Regex.Match(value, @"^R\d+\s*·\s*").Value;
            string signal = Regex.Match(value, @"PDmap[-_0-9]*|L-PRPD|PRPD|Q-PHI|N-PHI|U-Q-L|U-N-L|n-Q-U|n-N-U|Q-L|N-L|DAC-TD",
                RegexOptions.IgnoreCase).Value;
            string category = ReportLanguages.Text(language,
                image.Category == ReportImageCategory.RegionStatistics ? ReportLanguages.UiKeys.RegionalStatisticsChart :
                image.Category == ReportImageCategory.PdMap ? ReportLanguages.UiKeys.PdmapChart :
                image.Category == ReportImageCategory.Calibration ? ReportLanguages.UiKeys.CalibrationData :
                ReportLanguages.UiKeys.TestData);
            return marker + (string.IsNullOrEmpty(signal) ? string.Empty : signal + " · ") + category +
                " " + image.Sequence.ToString(CultureInfo.InvariantCulture);
        }

        internal static string RegionSummary(ReportImageInfo image, int language)
        {
            if (image == null) return string.Empty;
            string source = image.RegionSummary ?? string.Empty;
            if (language == 0 || !ContainsHan(source)) return source;
            Match match = Region.Match(source);
            if (match.Success)
                return match.Groups["id"].Value + " · " + match.Groups["source"].Value + " " +
                    ReportLanguages.Text(language, ReportLanguages.UiKeys.RegionSelection) + ": L " +
                    match.Groups["l"].Value.Trim() + "; Q " + match.Groups["q"].Value.Trim() + "; " +
                    match.Groups["rect"].Value + " " + ReportLanguages.Text(language, ReportLanguages.UiKeys.RegionRectangle) + "; " +
                    match.Groups["count"].Value + " " + ReportLanguages.Text(language, ReportLanguages.UiKeys.RegionRecords);
            string id = Regex.Match(source, @"R\d+").Value;
            return (string.IsNullOrEmpty(id) ? string.Empty : id + " · ") +
                ReportLanguages.Text(language, ReportLanguages.UiKeys.RegionSelection);
        }

        internal static ReportImageInfo ForRendering(ReportImageInfo image, int language)
        {
            return new ReportImageInfo
            {
                Sequence = image.Sequence,
                ChartKind = image.ChartKind,
                ChartMode = image.ChartMode,
                Category = image.Category,
                Title = Title(image, language),
                RegionSummary = RegionSummary(image, language),
                ImagePath = image.ImagePath,
                LanguageIndex = language
            };
        }
    }
}
