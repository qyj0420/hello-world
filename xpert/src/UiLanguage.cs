using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Xiongxin.OWTS.Localization
{
    // Shared source compiled into each independent UI assembly. Persist language
    // codes, not selector positions: legacy module indexes 0..17 never move.
    internal static class UiLanguage
    {
        internal const int TraditionalChinese = 18;
        internal const string SystemCode = "system";
        internal static readonly string[] Codes = { "zh-CN", "en", "bg", "hr", "cs", "fr", "de", "hu", "it", "pl", "ro", "ru", "sr", "sk", "sl", "es", "tr", "vi", "zh-TW", "pt-BR", "ja", "sv", "nl", "es-419", "en-GB", "de-CH", "fr-CH", "it-CH", "rm-CH", "be", "kk", "uz", "az", "hy", "ky", "tg", "tk", "uk", "ka", "pt-PT" };
        internal static readonly string[] Names = { "简体中文", "English", "Български", "Hrvatski", "Čeština", "Français", "Deutsch", "Magyar", "Italiano", "Polski", "Română", "Русский", "Српски", "Slovenčina", "Slovenščina", "Español", "Türkçe", "Tiếng Việt", "繁體中文", "Português (Brasil)", "日本語", "Svenska", "Nederlands", "Español (Latinoamérica)", "English (United Kingdom)", "Deutsch (Schweiz)", "Français (Suisse)", "Italiano (Svizzera)", "Rumantsch", "Беларуская", "Қазақша", "O‘zbekcha", "Azərbaycan dili", "Հայերեն", "Кыргызча", "Тоҷикӣ", "Türkmençe", "Українська", "ქართული", "Português (Portugal)" };
        internal static readonly string[] CultureNames = { "zh-CN", "en-US", "bg-BG", "hr-HR", "cs-CZ", "fr-FR", "de-DE", "hu-HU", "it-IT", "pl-PL", "ro-RO", "ru-RU", "sr-Cyrl-RS", "sk-SK", "sl-SI", "es-ES", "tr-TR", "vi-VN", "zh-TW", "pt-BR", "ja-JP", "sv-SE", "nl-NL", "es-AR", "en-GB", "de-CH", "fr-CH", "it-CH", "rm-CH", "be-BY", "kk-KZ", "uz-Latn-UZ", "az-Latn-AZ", "hy-AM", "ky-KG", "tg-Cyrl-TJ", "tk-TM", "uk-UA", "ka-GE", "pt-PT" };

        internal static string ResolveCode(string preference)
        { return ResolveCode(preference, CultureInfo.CurrentUICulture); }

        internal static string ResolveCode(string preference, CultureInfo systemCulture)
        {
            string code = preference;
            if (String.IsNullOrWhiteSpace(code) || String.Equals(code, SystemCode, StringComparison.OrdinalIgnoreCase))
                code = (systemCulture ?? CultureInfo.CurrentUICulture).Name;
            code = code.Trim().Replace('_', '-');
            foreach (string supported in Codes)
                if (supported.Equals(code, StringComparison.OrdinalIgnoreCase)) return supported;
            if (code.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                if (code.IndexOf("Hant", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    code.Equals("zh-TW", StringComparison.OrdinalIgnoreCase) ||
                    code.Equals("zh-HK", StringComparison.OrdinalIgnoreCase) ||
                    code.Equals("zh-MO", StringComparison.OrdinalIgnoreCase)) return "zh-TW";
                return "zh-CN";
            }
            if (code.StartsWith("pt", StringComparison.OrdinalIgnoreCase)) return "pt-BR";
            if (code.StartsWith("rm", StringComparison.OrdinalIgnoreCase)) return "rm-CH";
            if (code.StartsWith("es-", StringComparison.OrdinalIgnoreCase) && !code.Equals("es-ES", StringComparison.OrdinalIgnoreCase)) return "es-419";
            string neutral = code.Split('-')[0];
            foreach (string supported in Codes)
                if (supported.Equals(neutral, StringComparison.OrdinalIgnoreCase)) return supported;
            return "en";
        }

        internal static int Index(string preference)
        { return Array.IndexOf(Codes, ResolveCode(preference)); }

        internal static int ResolveIndex(int index)
        {
            if (index == -1) return Index(SystemCode);
            if (index < 0 || index >= Codes.Length) throw new ArgumentOutOfRangeException("index");
            return index;
        }

        internal static bool IsChinese(int index) { index = ResolveIndex(index); return index == 0 || index == TraditionalChinese; }
        internal static bool IsChinese(string code) { return IsChinese(Index(code)); }

        // Exact locale first, then region/script fallback. Unknown codes are not
        // silently accepted by public module setters.
        internal static int FindCode(string code)
        {
            if (String.IsNullOrWhiteSpace(code)) return -1;
            if (String.Equals(code, SystemCode, StringComparison.OrdinalIgnoreCase)) return Index(code);
            string normalized = code.Trim().Replace('_', '-');
            string root = normalized.Split('-')[0];
            foreach (string supported in Codes)
                if (supported.Split('-')[0].Equals(root, StringComparison.OrdinalIgnoreCase)) return Index(normalized);
            return -1;
        }

        internal static int LegacyIndex(int index)
        {
            switch (Codes[ResolveIndex(index)])
            {
                case "es-419": return 15;
                case "en-GB": return 1;
                case "de-CH": return 6;
                case "fr-CH": return 5;
                case "it-CH": return 8;
                default: return index >= 19 ? 1 : index;
            }
        }

        internal static bool TryTranslate(int index, string builtInEnglish, out string translated)
        { return InternationalText.TryGet(Codes[ResolveIndex(index)], builtInEnglish, out translated); }

        internal static string Translate(int index, string builtInEnglish)
        { string text; return TryTranslate(index, builtInEnglish, out text) ? text : InternationalText.Canonical(builtInEnglish); }

        internal static string BuiltIn(int index, string builtInText)
        {
            index = ResolveIndex(index);
            if (index == 0) return builtInText;
            if (index == TraditionalChinese) return ToTraditional(builtInText);
            return Translate(index, InternationalText.English(builtInText));
        }

        // Only call on built-in interface resources, NEVER user text, paths,
        // storage keys, project metadata, report conclusions or raw measurements.
        internal static string Text(int index, string chinese, string english)
        { index = ResolveIndex(index); return index == 0 ? chinese : index == TraditionalChinese ? ToTraditional(chinese) : Translate(index, english); }

        internal static string Display(string preference, string builtInText)
        { return BuiltIn(Index(preference), builtInText); }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int LCMapStringEx(string localeName, uint flags, string source, int sourceLength,
            StringBuilder destination, int destinationLength, IntPtr version, IntPtr reserved, IntPtr sortHandle);

        internal static string ToTraditional(string builtInText)
        {
            if (String.IsNullOrEmpty(builtInText)) return builtInText;
            const uint Traditional = 0x04000000;
            int size = LCMapStringEx("zh-TW", Traditional, builtInText, -1, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (size <= 0) throw new InvalidOperationException("Windows Chinese script conversion is unavailable.");
            var result = new StringBuilder(size);
            if (LCMapStringEx("zh-TW", Traditional, builtInText, -1, result, size, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) <= 0)
                throw new InvalidOperationException("Windows Chinese script conversion failed.");
            string text = result.ToString();
            // Windows preserves some ambiguous characters. Resolve known UI
            // phrases, not individual characters (e.g. 皇后 must remain 皇后).
            foreach (string phrase in new[] { "之后", "以後", "之後", "最後", "後續", "後臺", "稍後", "完成後", "保存後", "儲存後", "載入後", "加載後", "開啟後", "啟動後", "測試後", "試驗後", "校準後", "測量後", "修改後", "提示後", "前後", "後退", "後端", "後台" })
            {
                string traditional = phrase.Replace("后", "後");
                text = text.Replace(traditional.Replace("後", "后"), traditional);
            }
            foreach (string phrase in new[] { "多雲", "少雲", "雲量", "雲層", "雲端", "雲服務" })
                text = text.Replace(phrase.Replace("雲", "云"), phrase);
            text = text.Replace("大于", "大於").Replace("小于", "小於");
            return text;
        }
    }
}
