using System;
using System.ComponentModel;
using System.Drawing;
using Xiongxin.OWTS.Localization;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        private int languageIndex;
        [ThreadStatic] private static int drawingLanguage;
        [ThreadStatic] internal static Action<string> CaptionTranslationForTests;
        [DefaultValue(0)] public int LanguageIndex
        {
            get { return languageIndex; }
            set { int resolved = UiLanguage.ResolveIndex(value); RunOnUiThread(delegate { languageIndex = resolved; Invalidate(); }); }
        }
        public void SetLanguageCode(string code) { LanguageIndex = UiLanguage.Index(code); }
        // The calibration renderer's existing resources are Chinese. Only
        // program-owned captions pass through here; joint labels and file paths
        // are drawn separately and are never rewritten.
        private static string BuiltIn(string text)
        {
            string caption=UiLanguage.BuiltIn(drawingLanguage,text);
            var observe=CaptionTranslationForTests;if(observe!=null)observe(caption);
            return caption;
        }
        private void RenderFrame(Graphics graphics, Rectangle bounds)
        {
            int previous = drawingLanguage;
            drawingLanguage = languageIndex;
            try { RenderLocalizedFrame(graphics, bounds); }
            finally { drawingLanguage = previous; }
        }
    }
}
