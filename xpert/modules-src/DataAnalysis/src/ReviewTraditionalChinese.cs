using System;

namespace HVX.PdReview
{
    internal static class ReviewTraditionalChinese
    {
        // Call only with application-owned Chinese UI phrases, never project names,
        // source paths, IDs, waveform values or customer-entered report conclusions.
        internal static string Text(string builtInChinese)
        {
            return Xiongxin.OWTS.Localization.UiLanguage.ToTraditional(builtInChinese);
        }
    }
}
