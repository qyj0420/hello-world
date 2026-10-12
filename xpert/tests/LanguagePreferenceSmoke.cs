using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Xiongxin.OWTS.Desktop;
using Xiongxin.OWTS.Localization;

internal static class LanguagePreferenceSmoke
{
    static int checks;
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static void Check(bool condition, string text) { checks++; if (!condition) throw new Exception(text); }
    static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, Flags).GetValue(target); }
    sealed class QaForm : Form { protected override bool ShowWithoutActivation { get { return true; } } }
    [STAThread] static int Main(string[] args)
    {
        var previous = Thread.CurrentThread.CurrentUICulture;
        try
        {
            string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            Check(new AppSettings().LanguageCode == "system", "New settings follow system");
            Check(AppLanguages.SelectionItems[0].Code == "system", "Follow system is first item");
            Check(AppLanguages.SelectionItems.Length == 41, "System plus 40 locales");
            Check(AppLanguages.All[18].Code == "zh-TW", "Old 0..17 indexes stable");
            foreach (var pair in new[] { "zh-CN|zh-CN", "zh-SG|zh-CN", "zh-TW|zh-TW", "zh-HK|zh-TW", "zh-MO|zh-TW", "zh-Hant|zh-TW", "en-US|en", "de-DE|de", "fr-CA|fr", "vi-VN|vi", "ja-JP|ja", "ar-SA|en" })
            {
                var parts = pair.Split('|'); var culture = CultureInfo.GetCultureInfo(parts[0]);
                Check(UiLanguage.ResolveCode("system", culture) == parts[1], "System mapping " + parts[0]);
                Check(UiLanguage.ResolveCode("zh-CN", culture) == "zh-CN", "Explicit setting unaffected by system");
            }
            for (int n = 0; n < 100; n++)
            {
                Check(UiLanguage.ToTraditional("生成报告") == "生成報告", "Conversion terminator and exact length");
                Check(UiLanguage.ToTraditional("局部多云 天气获取 后台记录") == "局部多雲 天氣獲取 後臺記錄", "Ambiguous UI phrases");
                Check(UiLanguage.ToTraditional("皇后") == "皇后", "Not indiscriminate character substitution");
            }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            foreach (string selected in new[] { "system", "zh-CN", "zh-TW", "en", "de", "vi" })
            {
                Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("zh-TW");
                var settings = new AppSettings { LanguageCode = selected };
                var path = Path.Combine(output, selected + ".xml"); settings.Save(path);
                Check(AppSettings.Load(path).LanguageCode == selected, "Preference round trip " + selected);
                using (var dialog = new SettingsForm(settings))
                {
                    var combo = Field<ComboBox>(dialog, "language");
                    Check(((LanguageItem)combo.SelectedItem).Code == selected, "Selector preserves preference");
                    Check(((LanguageItem)combo.Items[0]).Code == "system", "UI first option");
                    combo.SelectedIndex = AppLanguages.SelectionIndex("zh-TW");
                    Check(((LanguageItem)combo.SelectedItem).Code == "zh-TW", "Explicit traditional selection");
                    if (selected == "system" || selected == "zh-TW") Check(dialog.Text == "參數設置", "Traditional settings title");
                }
                using (var form = new MainForm(settings, new ApplicationState(), true))
                {
                    form.CreateControl(); form.PerformLayout();
                    string project = Field<Label>(form, "projectStatus").Text;
                    if (selected == "system" || selected == "zh-TW") Check(project == "當前項目", "Traditional home title");
                    foreach (string name in new[] { "settingsButton", "helpButton", "openProjectButton", "refreshDeviceButton", "backButton" })
                    {
                        var button = Field<Button>(form, name);
                        int need = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
                        Check(button.Width >= need + button.Padding.Horizontal, "Full button text " + selected + ":" + name);
                        Check(!button.AutoEllipsis, "No button ellipsis");
                    }
                    if (selected == "zh-TW") using (var bitmap = new Bitmap(form.Width, form.Height))
                    { form.DrawToBitmap(bitmap, form.ClientRectangle); bitmap.Save(Path.Combine(output, "traditional-home.png")); }
                }
            }
            Console.WriteLine("LANGUAGE_PREFERENCE_PASS=" + checks); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Thread.CurrentThread.CurrentUICulture = previous; }
    }
}
