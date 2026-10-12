namespace HVX.ReportGenerator
{
    internal static partial class ReportLanguages
    {
        internal static bool IsChinese(int language) { return Xiongxin.OWTS.Localization.UiLanguage.IsChinese(language); }
        // Only call this for the application's built-in resource literals. Never
        // apply script conversion to names, paths, archived text or measurements.
        internal static string TraditionalResource(string simplifiedResource)
        {
            return Xiongxin.OWTS.Localization.UiLanguage.ToTraditional(simplifiedResource);
        }
        internal static string Ui(int language, string chineseResource, string englishResource)
        {
            return Text(language, englishResource, chineseResource);
        }
    }
}
